using System.IO.Pipes;
using System.Runtime.ExceptionServices;

namespace Ability.Hosting;

/// <summary>
/// 单次运行对象的准备阶段：创建本次组件与资源，并在准备失败时按创建顺序反向释放、发布完成报告。
/// </summary>
internal sealed partial class HostRun
{
    /// <summary>
    /// 准备失败清理的结果：目录报告、汇总问题与是否存在释放失败。
    /// </summary>
    /// <param name="TempReport">目录收尾报告。</param>
    /// <param name="Issues">汇总的问题列表。</param>
    /// <param name="ReleaseFailed">是否存在资源释放失败。</param>
    private readonly record struct PreparationCleanupResult(TaskTempReport? TempReport, List<HostCleanupIssue> Issues, bool ReleaseFailed);

    /// <summary>
    /// 创建本次运行的回调派发器、治理协调器、登记表与根记录、启动准备、任务专属目录与根命名管道。
    /// 任一步失败时停止创建，已创建对象由准备失败清理路径反向释放。
    /// </summary>
    /// <param name="error">准备阶段抛出的异常；成功时为 null。</param>
    /// <returns>准备成功时为 true。</returns>
    private bool TryPrepare(out Exception? error)
    {
        try
        {
            _dispatcher = new HostCallbackDispatcher(_options);
            _governance = new HostGovernanceCoordinator(msg => Dispatcher.DispatchDiagnostic(msg));
            _registry = new InvocationRegistry();
            _rootRecord = Registry.Register(_invocation.TaskId, null, _rootKey);
            _launcher = LauncherPreparation.Prepare(_invocation.LauncherPath, _invocation.WorkingDirectory);
            _tempDirectory = TaskTempDirectory.Create(_options.TaskTempRootDirectory, _invocation.TaskId);
            _pipeServer = new NamedPipeServerStream(
                _rootKey, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    /// <summary>
    /// 在 2 秒前台预算内执行准备失败清理并发布完成报告，随后重抛原准备异常。
    /// 前台预算耗尽时由后台延续在清理与迟到问题收集结束后发布最终报告。
    /// </summary>
    /// <param name="preparationError">准备阶段抛出的异常。</param>
    /// <returns>清理与报告发布的异步完成任务。</returns>
    private async Task HandlePreparationFailureAsync(Exception preparationError)
    {
        _issues.Add(new HostCleanupIssue("Preparation", preparationError.GetType().Name, DescribeSafeError(preparationError)));

        var cleanupTask = Task.Run(() => PerformPreparationCleanupAsync());
        using var finiteCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var drainTimedOut = false;
        TaskTempReport? tempReport = null;
        List<HostCleanupIssue> issues = [.. _issues];

        try
        {
            var result = await cleanupTask.WaitAsync(finiteCts.Token).ConfigureAwait(false);
            tempReport = result.TempReport;
            issues = result.Issues;
            _preparationReleaseFailed = result.ReleaseFailed;
        }
        catch (OperationCanceledException) when (finiteCts.IsCancellationRequested && !cleanupTask.IsCompleted)
        {
            drainTimedOut = true;
            issues.Add(new HostCleanupIssue(
                "PreparationDrainTimeout",
                "TimeoutException",
                "准备失败资源回收前台等待超时（>2s），实际清理在后台继续执行。"));
        }
        catch (Exception ex)
        {
            issues.Add(new HostCleanupIssue("PreparationCleanup", ex.GetType().Name, DescribeSafeError(ex)));
            _preparationReleaseFailed = true;
        }

        if (drainTimedOut)
        {
            RegisterLatePreparationCompletion(cleanupTask, tempReport, issues.ToArray());
        }
        else
        {
            _lifetime.Complete(BuildPreparationReport(tempReport, issues));
        }

        ExceptionDispatchInfo.Capture(preparationError).Throw();
    }

    /// <summary>
    /// 反向释放准备阶段已创建的资源：命名管道、任务专属目录与回调派发器。
    /// </summary>
    /// <returns>目录报告、汇总问题与释放失败事实。</returns>
    private async Task<PreparationCleanupResult> PerformPreparationCleanupAsync()
    {
        var issues = new List<HostCleanupIssue>(_issues);
        var releaseFailed = false;

        if (_pipeServer is not null)
        {
            try
            {
                await _pipeServer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                issues.Add(new HostCleanupIssue("PipeServer", ex.GetType().Name, DescribeSafeError(ex)));
                releaseFailed = true;
            }
        }

        TaskTempReport? tempReport = null;
        if (_tempDirectory is not null)
        {
            try
            {
                tempReport = await _tempDirectory.FinalizeAsync(null, TimeSpan.Zero, wholeTreeExitConfirmed: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                issues.Add(new HostCleanupIssue("TaskTemp", ex.GetType().Name, DescribeSafeError(ex)));
                releaseFailed = true;
            }

            try
            {
                await _tempDirectory.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                issues.Add(new HostCleanupIssue("TaskTempDispose", ex.GetType().Name, DescribeSafeError(ex)));
                releaseFailed = true;
            }
        }

        if (_dispatcher is not null)
        {
            try
            {
                await _dispatcher.WaitAllDrainedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                issues.Add(new HostCleanupIssue("CallbackDispatcher", ex.GetType().Name, DescribeSafeError(ex)));
                releaseFailed = true;
            }
        }

        return new PreparationCleanupResult(tempReport, issues, releaseFailed);
    }

    /// <summary>
    /// 登记准备失败清理的后台延续，在清理与迟到问题收集结束后发布最终报告。
    /// </summary>
    /// <param name="cleanupTask">正在执行的准备清理任务。</param>
    /// <param name="timeoutTempReport">前台超时时已知的目录报告。</param>
    /// <param name="timeoutIssues">前台超时时已知的问题快照。</param>
    private void RegisterLatePreparationCompletion(
        Task<PreparationCleanupResult> cleanupTask,
        TaskTempReport? timeoutTempReport,
        HostCleanupIssue[] timeoutIssues)
    {
        _ = cleanupTask.ContinueWith(
            task =>
            {
                var mergedIssues = new List<HostCleanupIssue>(timeoutIssues);
                var finalTempReport = timeoutTempReport;
                var releaseFailed = _preparationReleaseFailed;

                if (task.Status == TaskStatus.RanToCompletion)
                {
                    var result = task.Result;
                    finalTempReport = result.TempReport ?? finalTempReport;
                    releaseFailed |= result.ReleaseFailed;

                    foreach (var issue in result.Issues)
                    {
                        if (!mergedIssues.Contains(issue))
                        {
                            mergedIssues.Add(issue);
                        }
                    }
                }
                else if (task.IsFaulted && task.Exception is not null)
                {
                    releaseFailed = true;
                    foreach (var inner in task.Exception.Flatten().InnerExceptions)
                    {
                        var issue = new HostCleanupIssue("PreparationCleanupBackground", inner.GetType().Name, DescribeSafeError(inner));
                        if (!mergedIssues.Contains(issue))
                        {
                            mergedIssues.Add(issue);
                        }
                    }
                }

                _lifetime.Complete(new HostCleanupReport
                {
                    ProcessExitConfirmed = true,
                    GovernanceResourcesReclaimed = true,
                    DiagnosticsCompleted = true,
                    DeferredWorkCompleted = true,
                    CleanupCompleted = IsTempCompliant(finalTempReport) && !releaseFailed,
                    TaskTemp = finalTempReport,
                    Issues = mergedIssues.AsReadOnly()
                });
            },
            TaskScheduler.Default);
    }

    /// <summary>
    /// 构造准备失败路径的完成报告：未启动进程，诊断与受管工作按已收尾处理。
    /// </summary>
    /// <param name="tempReport">清理阶段得到的目录报告。</param>
    /// <param name="issues">清理阶段汇总的问题列表。</param>
    /// <returns>准备失败路径的完成报告。</returns>
    private HostCleanupReport BuildPreparationReport(TaskTempReport? tempReport, List<HostCleanupIssue> issues)
    {
        return new HostCleanupReport
        {
            ProcessExitConfirmed = true,
            GovernanceResourcesReclaimed = true,
            DiagnosticsCompleted = true,
            DeferredWorkCompleted = true,
            CleanupCompleted = IsTempCompliant(tempReport) && !_preparationReleaseFailed,
            TaskTemp = tempReport,
            Issues = issues.AsReadOnly()
        };
    }
}
