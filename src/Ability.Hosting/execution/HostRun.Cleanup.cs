using System.Diagnostics;

namespace Ability.Hosting;

/// <summary>
/// 单次运行对象的收尾阶段：前台必要停止与处置、快照事实，以及前台返回后的同一所有者后台收尾。
/// </summary>
internal sealed partial class HostRun
{
    /// <summary>
    /// 前台收尾：收口未进入启动流程的根记录，再排空自然退出任务、停止子调用树与根进程、结算治理、
    /// 处置目录、导出诊断并关闭连接。各步骤自身记录问题并继续后续步骤，
    /// 因此一次调用不会跳过后面的清理；全部步骤结束后置位完成事实。
    /// </summary>
    /// <returns>前台收尾的异步完成任务。</returns>
    private async Task PerformForegroundCleanupAsync()
    {
        CloseOutUnstartedRoot();
        await DrainNaturalExitTasksAsync().ConfigureAwait(false);
        await StopChildTreeAndRootAsync().ConfigureAwait(false);
        SettleGovernance();
        ResolveGovernanceResources();
        await FinalizeTaskTempAsync().ConfigureAwait(false);
        ApplyActivityStatus();
        await ExportDiagnosticsAsync().ConfigureAwait(false);
        await CloseConnectionAsync().ConfigureAwait(false);
        _foregroundCleanupCompleted = true;
    }

    /// <summary>
    /// 对确认从未进入根启动流程的记录完成未启动回收与启动完成通知，使整树停止不必等待不会发生的启动结果。
    /// 已进入启动流程的记录保持原状态，由进程护栏按真实启动结果回收；
    /// 只有本运行对象知道根记录是否进入过启动流程，因此不依据进程句柄是否为空判断。
    /// </summary>
    private void CloseOutUnstartedRoot()
    {
        if (_rootLaunchEntered)
        {
            return;
        }

        RootRecord.MarkNonStartedAsReaped();
        RootRecord.CompleteLaunch();
    }

    /// <summary>
    /// 在有限预算内等待自然退出回收任务排空。
    /// </summary>
    /// <returns>等待排空的异步完成任务。</returns>
    private async Task DrainNaturalExitTasksAsync()
    {
        Task[] pending;
        lock (_naturalExitLock)
        {
            pending = [.. _naturalExitTasks];
        }

        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(2), _options.TimeProvider).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 停止整棵子调用树并回收根进程，随后按权威记录确认整树退出事实。
    /// 尚未建立子调用管理器的路径没有已登记子调用，只回收根记录。
    /// </summary>
    /// <returns>停止与回收的异步完成任务。</returns>
    private async Task StopChildTreeAndRootAsync()
    {
        try
        {
            if (_childCalls is not null)
            {
                var revoked = await _childCalls.StopAllAsync(_rootKey).ConfigureAwait(false);
                Governance.RevokeLeases(revoked);
            }
        }
        catch (Exception ex)
        {
            _issues.Add(new HostCleanupIssue("ChildStopAll", ex.GetType().Name, DescribeSafeError(ex)));
            _childCallsReleaseFailed = true;
            Dispatcher.DispatchDiagnostic($"[Host] 子调用整树停止异常: {ex.GetType().Name}");
        }

        try
        {
            _rootExitedNaturally = await ProcessGuard.StopAsync(RootRecord, _options.CancelGracePeriod).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _issues.Add(new HostCleanupIssue("ProcessGuardStop", ex.GetType().Name, DescribeSafeError(ex)));
            _processReleaseFailed = true;
            Dispatcher.DispatchDiagnostic($"[Host] 根进程停止异常: {ex.GetType().Name}");
        }

        _rootExitConfirmed = RootRecord.State == InvocationState.Reaped;
        var treeAllReaped = Registry.AllInvocationsReaped(_rootKey);
        _exitConfirmed = _rootExitConfirmed && treeAllReaped;
        _finalExitCode = _rootExitConfirmed ? RootRecord.ExitCode : null;
    }

    /// <summary>
    /// 中止未决治理请求，并按进程退出事实收口受管操作。
    /// </summary>
    private void SettleGovernance()
    {
        try
        {
            Governance.AbortPendingRequests();
        }
        catch (Exception ex)
        {
            _issues.Add(new HostCleanupIssue("GovernanceAbort", ex.GetType().Name, DescribeSafeError(ex)));
            _governanceReleaseFailed = true;
        }

        try
        {
            Governance.EndOperationsForProcessExit(
                Registry,
                RootRecord.ExitCode ?? -1,
                _rootExitedNaturally,
                snap => Dispatcher.DispatchSnapshot(snap),
                diag => Dispatcher.DispatchDiagnostic(diag));
        }
        catch (Exception ex)
        {
            _issues.Add(new HostCleanupIssue("GovernanceEndOperations", ex.GetType().Name, DescribeSafeError(ex)));
            _governanceReleaseFailed = true;
        }
    }

    /// <summary>
    /// 按整树退出事实回收或保留租约，并确认治理资源结算结果与未定终态。
    /// 本步骤异常只记问题并置位释放失败事实，不阻止后续收尾步骤。
    /// </summary>
    private void ResolveGovernanceResources()
    {
        try
        {
            if (!_exitConfirmed)
            {
                Dispatcher.DispatchDiagnostic("[Warning] 进程树强杀后超时仍未全部确认退出，保留活动资源未注销状态以防外部冲突。");

                if (_outcome == "Unknown")
                {
                    _outcome = "ProcessUnresponsive";
                    _faultKind ??= "ProcessKillTimeout";
                    _faultMessage = "进程树强杀后超时仍未全部退出。";
                }
            }
            else
            {
                try
                {
                    Governance.RecycleAllLeases();
                }
                catch (Exception ex)
                {
                    _issues.Add(new HostCleanupIssue("GovernanceRecycleLeases", ex.GetType().Name, DescribeSafeError(ex)));
                    _governanceReleaseFailed = true;
                }
            }

            _governanceResourcesReclaimed = _exitConfirmed && Governance.ActiveLeases.Count == 0;

            if (_outcome == "Unknown")
            {
                _outcome = (_rootExitConfirmed && RootRecord.ExitCode == 0) ? "ProcessExitWithoutResult" : "ProcessCrash";
            }
        }
        catch (Exception ex)
        {
            _issues.Add(new HostCleanupIssue("GovernanceResources", ex.GetType().Name, DescribeSafeError(ex)));
            _governanceReleaseFailed = true;
        }
    }

    /// <summary>
    /// 收尾任务专属临时目录，并按消费事实裁决结果交付条件；报告取目录组件的实际快照。
    /// </summary>
    /// <returns>目录收尾的异步完成任务。</returns>
    private async Task FinalizeTaskTempAsync()
    {
        if (_tempDirectory is not null)
        {
            try
            {
                _tempReport = await _tempDirectory.FinalizeAsync(
                    _options.TaskTempConsumer,
                    _options.TaskTempConsumerTimeout,
                    _exitConfirmed).ConfigureAwait(false);
                RootRecord.TaskTempReport = _tempReport;
            }
            catch (Exception ex)
            {
                Dispatcher.DispatchDiagnostic($"临时目录收尾异常: {DescribeSafeError(ex)}");
                _issues.Add(new HostCleanupIssue("TaskTempFinalize", ex.GetType().Name, DescribeSafeError(ex)));
                _tempDirectoryReleaseFailed = true;
                _tempReport = _tempDirectory.Report?.Snapshot();
                RootRecord.TaskTempReport = _tempReport;
            }
        }

        if (_tempReport is not null && _tempReport.ConsumptionStatus is "Failed" or "TimedOut" && _outcome is "SubmitResult" or "ResultClose")
        {
            _outcome = "ConsumerFault";
            _faultKind ??= _tempReport.ConsumptionStatus == "TimedOut" ? "TaskTempConsumerTimeout" : "TaskTempConsumerFailed";
            _faultMessage ??= _tempReport.RetentionReason ?? "任务专属临时目录转存消费失败或超时。";
        }
    }

    /// <summary>
    /// 按结果交付条件设置根 Activity 状态并结束活动；本步骤异常只记问题，不阻止后续收尾步骤。
    /// </summary>
    private void ApplyActivityStatus()
    {
        try
        {
            bool consumerOk = _tempReport is null || _tempReport.ConsumptionStatus is "NotRequested" or "Succeeded";
            bool isSuccess = _outcome is "SubmitResult" or "ResultClose"
                && (_execResult?.BusinessOutcome is "SubmitResult" or "ResultClose" || (_execResult is null && _outcome is "SubmitResult" or "ResultClose"))
                && _exitConfirmed
                && _governanceResourcesReclaimed
                && _finalExitCode == 0
                && consumerOk;

            if (isSuccess)
            {
                _hostActivity?.SetStatus(ActivityStatusCode.Ok);
            }
            else
            {
                _hostActivity?.SetStatus(ActivityStatusCode.Error, _faultMessage ?? _outcome);
            }

            _hostActivity?.Stop();
        }
        catch (Exception ex)
        {
            _issues.Add(new HostCleanupIssue("HostActivity", ex.GetType().Name, DescribeSafeError(ex)));
        }
    }

    /// <summary>
    /// 导出全链路追踪并排空回调派发器；导出失败不改变业务结果。
    /// 尚未建立根活动的路径没有可导出的调用链，只排空派发器。
    /// </summary>
    /// <returns>导出与排空的异步完成任务。</returns>
    private async Task ExportDiagnosticsAsync()
    {
        if (_hostActivity is not null)
        {
            try
            {
                await HostTraceCoordinator.CollectAndExportAsync(
                    Registry,
                    _options,
                    _hostActivity,
                    msg => Dispatcher.DispatchDiagnostic(msg),
                    issue => _issues.Add(issue)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _traceExportError = ex;
                Dispatcher.DispatchDiagnostic($"Trace 导出异常: {DescribeSafeError(ex)}");
            }
        }

        try
        {
            await Dispatcher.CompleteAndDrainAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _issues.Add(new HostCleanupIssue("CallbackDispatcher", ex.GetType().Name, DescribeSafeError(ex)));
            _dispatcherReleaseFailed = true;
        }
    }

    /// <summary>
    /// 按终态映射的终止原因关闭根连接。
    /// </summary>
    /// <returns>关闭连接的异步完成任务。</returns>
    private async Task CloseConnectionAsync()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            await _connection.CloseAsync(ResolveTerminationReason(_outcome)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _issues.Add(new HostCleanupIssue("ConnectionClose", ex.GetType().Name, DescribeSafeError(ex)));
            _connectionReleaseFailed = true;
        }
    }

    /// <summary>
    /// 启动后台收尾：同一运行对象继续持有原组件，等实际使用者与派生清理退出后发布最终报告。
    /// 先等待未预期失败路径中仍在执行的前台收尾任务实际退出，再取得稳定问题快照，
    /// 使该任务期间记录的迟到释放错误同样进入最终报告。
    /// 调度失败不使用完成信号通知：此时没有发布者，信号保持待完成，如实表达实际使用尚未确认结束。
    /// </summary>
    private void StartBackgroundCleanup()
    {
        _ = Task.Run(async () =>
        {
            await WaitPendingForegroundCleanupAsync().ConfigureAwait(false);
            _finalIssues = [.. _issues];

            try
            {
                await WaitCoordinatorExitAsync().ConfigureAwait(false);
                await WaitChildCallTasksAsync().ConfigureAwait(false);
                await WaitNaturalExitTasksAsync().ConfigureAwait(false);
                await ReapRootProcessAsync().ConfigureAwait(false);
                await SettleLeasesAfterExitAsync().ConfigureAwait(false);
                await WaitOutputDrainAsync().ConfigureAwait(false);
                await WaitGovernanceDrainAsync().ConfigureAwait(false);
                await WaitConsumerTaskAsync().ConfigureAwait(false);
                await ReleaseConnectionAsync().ConfigureAwait(false);
                await FlushDispatcherAsync().ConfigureAwait(false);
                ReleaseCancellationSources();
                await ReleaseOwnedResourcesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AddFinalIssue(new HostCleanupIssue("HostLifetime", ex.GetType().Name, DescribeSafeError(ex)));
                _hostLifetimeReleaseFailed = true;
            }
            finally
            {
                PublishFinalReport();
            }
        });

        _backgroundCleanupStarted = true;
    }

    /// <summary>
    /// 等待未预期失败路径中仍在执行的前台收尾任务实际退出；其异常在取得问题快照前追加到本层问题列表，
    /// 由快照带入最终报告，不并发复制仍在修改的列表。
    /// </summary>
    /// <returns>等待完成的异步完成任务。</returns>
    private async Task WaitPendingForegroundCleanupAsync()
    {
        if (_pendingForegroundCleanup is null)
        {
            return;
        }

        try
        {
            await _pendingForegroundCleanup.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _issues.Add(new HostCleanupIssue("HostRun", ex.GetType().Name, DescribeSafeError(ex)));
        }
    }

    /// <summary>
    /// 等待执行协调器物理退出。
    /// </summary>
    /// <returns>等待的异步完成任务。</returns>
    private async Task WaitCoordinatorExitAsync()
    {
        if (_coordinator is null)
        {
            return;
        }

        try
        {
            await _coordinator.WaitForPhysicalExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AddFinalIssue(new HostCleanupIssue("ExecutionCoordinator", ex.GetType().Name, DescribeSafeError(ex)));
        }
    }

    /// <summary>
    /// 等待全部子调用任务与派生回收结束。
    /// </summary>
    /// <returns>等待的异步完成任务。</returns>
    private async Task WaitChildCallTasksAsync()
    {
        if (_childCalls is null)
        {
            return;
        }

        try
        {
            await _childCalls.WaitAllChildTasksAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AddFinalIssue(new HostCleanupIssue("ChildCalls", ex.GetType().Name, DescribeSafeError(ex)));
            _childCallsReleaseFailed = true;
        }
    }

    /// <summary>
    /// 等待自然退出回收任务持续归零，包含等待期间新派生的任务。
    /// </summary>
    /// <returns>等待的异步完成任务。</returns>
    private async Task WaitNaturalExitTasksAsync()
    {
        while (true)
        {
            Task[] remaining;
            lock (_naturalExitLock)
            {
                remaining = [.. _naturalExitTasks];
            }

            if (remaining.Length == 0)
            {
                break;
            }

            try
            {
                await Task.WhenAll(remaining).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AddFinalIssue(new HostCleanupIssue("NaturalExit", ex.GetType().Name, DescribeSafeError(ex)));
            }
        }

        lock (_naturalExitLock)
        {
            foreach (var issue in _naturalExitIssues)
            {
                AddFinalIssue(issue);
            }
        }
    }

    /// <summary>
    /// 等待根进程退出观察任务，并对迟到自然退出执行回收。
    /// </summary>
    /// <returns>等待与回收的异步完成任务。</returns>
    private async Task ReapRootProcessAsync()
    {
        if (RootRecord.ProcessExitTask is not { } rootExitTask)
        {
            return;
        }

        try
        {
            await rootExitTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AddFinalIssue(new HostCleanupIssue("ProcessExit", ex.GetType().Name, DescribeSafeError(ex)));
            _processReleaseFailed = true;
        }

        if (RootRecord.State == InvocationState.Reaped)
        {
            return;
        }

        try
        {
            await ProcessGuard.ReapExitedAsync(RootRecord).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AddFinalIssue(new HostCleanupIssue("RootLateReap", ex.GetType().Name, DescribeSafeError(ex)));
            _processReleaseFailed = true;
        }
    }

    /// <summary>
    /// 整树退出后回收全部租约，否则按已确认退出的记录撤销所属租约。
    /// </summary>
    /// <returns>租约结算的异步完成任务。</returns>
    private Task SettleLeasesAfterExitAsync()
    {
        bool allTreeReaped = RootRecord.State == InvocationState.Reaped
            && (_registry is null || Registry.AllInvocationsReaped(_rootKey));

        try
        {
            if (allTreeReaped)
            {
                _governance?.RecycleAllLeases();
            }
            else
            {
                var records = _registry?.AllRecords ?? [RootRecord];
                var reapedLeaseIds = records
                    .Where(record => record.State == InvocationState.Reaped)
                    .SelectMany(record => record.CleanLeaseIds())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                if (reapedLeaseIds.Length > 0)
                {
                    _governance?.RevokeLeases(reapedLeaseIds);
                }
            }
        }
        catch (Exception ex)
        {
            AddFinalIssue(new HostCleanupIssue(allTreeReaped ? "GovernanceRecycleLeases" : "GovernanceRevokeLeases", ex.GetType().Name, DescribeSafeError(ex)));
            _governanceReleaseFailed = true;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 等待全部调用记录的标准输出与标准错误排空任务结束。
    /// </summary>
    /// <returns>等待的异步完成任务。</returns>
    private async Task WaitOutputDrainAsync()
    {
        var records = _registry?.AllRecords ?? [RootRecord];

        foreach (var record in records)
        {
            if (record.StandardOutputTask is { } outputTask)
            {
                try
                {
                    await outputTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AddFinalIssue(new HostCleanupIssue($"StandardOutput_{record.InvocationKey}", ex.GetType().Name, DescribeSafeError(ex)));
                }
            }

            if (record.StandardErrorTask is { } errorTask)
            {
                try
                {
                    await errorTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AddFinalIssue(new HostCleanupIssue($"StandardError_{record.InvocationKey}", ex.GetType().Name, DescribeSafeError(ex)));
                }
            }
        }
    }

    /// <summary>
    /// 等待治理工作者排空与治理资源释放。
    /// </summary>
    /// <returns>等待的异步完成任务。</returns>
    private async Task WaitGovernanceDrainAsync()
    {
        if (_governance is null)
        {
            return;
        }

        try
        {
            await _governance.WaitAllDrainedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AddFinalIssue(new HostCleanupIssue("Governance", ex.GetType().Name, DescribeSafeError(ex)));
            _governanceReleaseFailed = true;
        }
    }

    /// <summary>
    /// 等待迟到的目录消费任务结束。
    /// </summary>
    /// <returns>等待的异步完成任务。</returns>
    private async Task WaitConsumerTaskAsync()
    {
        if (_tempDirectory?.ActiveConsumerTask is not { } consumerTask)
        {
            return;
        }

        try
        {
            await consumerTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AddFinalIssue(new HostCleanupIssue("TaskTempConsumer", ex.GetType().Name, DescribeSafeError(ex)));
        }
    }

    /// <summary>
    /// 等待连接关闭并释放连接及其清理任务。
    /// </summary>
    /// <returns>连接释放的异步完成任务。</returns>
    private async Task ReleaseConnectionAsync()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            await _connection.WaitClosedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AddFinalIssue(new HostCleanupIssue("ConnectionWaitClosed", ex.GetType().Name, DescribeSafeError(ex)));
        }

        if (_connection.CleanupFailure is { } waitError)
        {
            AddFinalIssue(new HostCleanupIssue("Connection", waitError.GetType().Name, DescribeSafeError(waitError)));
            _connectionReleaseFailed = true;
        }

        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AddFinalIssue(new HostCleanupIssue("ConnectionDispose", ex.GetType().Name, DescribeSafeError(ex)));
            _connectionReleaseFailed = true;
        }

        if (_connection.DisposalCleanupTask is { } disposalCleanupTask)
        {
            try
            {
                await disposalCleanupTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AddFinalIssue(new HostCleanupIssue("ConnectionDisposalCleanup", ex.GetType().Name, DescribeSafeError(ex)));
            }
        }

        if (_connection.CleanupFailure is { } disposalError)
        {
            AddFinalIssue(new HostCleanupIssue("Connection", disposalError.GetType().Name, DescribeSafeError(disposalError)));
            _connectionReleaseFailed = true;
        }
    }

    /// <summary>
    /// 等待回调派发器全部排空。
    /// </summary>
    /// <returns>等待的异步完成任务。</returns>
    private async Task FlushDispatcherAsync()
    {
        try
        {
            await Dispatcher.WaitAllDrainedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AddFinalIssue(new HostCleanupIssue("CallbackDispatcher", ex.GetType().Name, DescribeSafeError(ex)));
            _dispatcherReleaseFailed = true;
        }
    }

    /// <summary>
    /// 释放执行与关联取消源。
    /// </summary>
    private void ReleaseCancellationSources()
    {
        try
        {
            _linkedCts?.Dispose();
        }
        catch (Exception ex)
        {
            AddFinalIssue(new HostCleanupIssue("LinkedCtsDispose", ex.GetType().Name, DescribeSafeError(ex)));
            _cancellationSourceReleaseFailed = true;
        }

        try
        {
            _executionCts?.Dispose();
        }
        catch (Exception ex)
        {
            AddFinalIssue(new HostCleanupIssue("ExecutionCtsDispose", ex.GetType().Name, DescribeSafeError(ex)));
            _cancellationSourceReleaseFailed = true;
        }
    }

    /// <summary>
    /// 释放命名管道与任务专属目录。
    /// </summary>
    /// <returns>释放自有资源的异步完成任务。</returns>
    private async Task ReleaseOwnedResourcesAsync()
    {
        if (_pipeServer is not null)
        {
            try
            {
                await _pipeServer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AddFinalIssue(new HostCleanupIssue("PipeServerDispose", ex.GetType().Name, DescribeSafeError(ex)));
                _pipeServerReleaseFailed = true;
            }
        }

        if (_tempDirectory is not null)
        {
            try
            {
                await _tempDirectory.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AddFinalIssue(new HostCleanupIssue("TaskTempDispose", ex.GetType().Name, DescribeSafeError(ex)));
                _tempDirectoryReleaseFailed = true;
            }
        }
    }

    /// <summary>
    /// 发布最终完成报告：按权威退出记录、治理结算与自有资源释放事实汇总，并合并迟到问题。
    /// </summary>
    private void PublishFinalReport()
    {
        if (_dispatcher is not null)
        {
            foreach (var issue in _dispatcher.GetCapturedIssues())
            {
                AddFinalIssue(issue);
            }
        }

        if (_governance is not null)
        {
            foreach (var issue in _governance.GetCapturedIssues())
            {
                AddFinalIssue(issue);
            }
        }

        bool backgroundExitConfirmed = _rootRecord?.State == InvocationState.Reaped
            && (_registry is null || _registry.AllInvocationsReaped(_rootKey));
        bool backgroundGovernanceReclaimed = backgroundExitConfirmed
            && (_governance is null || _governance.ActiveLeases.Count == 0);
        var backgroundTemp = _tempDirectory?.Report?.Snapshot() ?? _tempReport?.Snapshot();

        _lifetime.Complete(new HostCleanupReport
        {
            ProcessExitConfirmed = backgroundExitConfirmed,
            GovernanceResourcesReclaimed = backgroundGovernanceReclaimed,
            DiagnosticsCompleted = true,
            DeferredWorkCompleted = true,
            CleanupCompleted = backgroundExitConfirmed
                && backgroundGovernanceReclaimed
                && IsTempCompliant(backgroundTemp)
                && OwnResourcesReleased,
            TaskTemp = backgroundTemp,
            Issues = _finalIssues.AsReadOnly()
        });
    }

    /// <summary>
    /// 把迟到问题并入后台收尾问题列表，重复项只保留一次。
    /// </summary>
    /// <param name="issue">迟到问题。</param>
    private void AddFinalIssue(HostCleanupIssue issue)
    {
        if (!_finalIssues.Contains(issue))
        {
            _finalIssues.Add(issue);
        }
    }
}
