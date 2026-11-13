using System.Collections.Concurrent;
using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// 宿主端子调用协调器，负责子调用登记、进程异步准备与启动、并发整树停止与自然退出观察。
/// </summary>
/// <param name="registry">权威调用登记表。</param>
/// <param name="buildTimeout">构建与准备时限。</param>
/// <param name="cleanupGracePeriod">退出等待宽限期。</param>
/// <param name="hostCancellation">宿主生命周期取消令牌。</param>
/// <param name="timeProvider">启动预算的时间来源。</param>
/// <param name="processExited">进程退出通知回调。</param>
/// <param name="diagnostic">诊断日志通知回调。</param>
/// <param name="processStarted">子进程启动通知回调。</param>
internal sealed class HostChildCalls(
    InvocationRegistry registry,
    TimeSpan buildTimeout,
    TimeSpan cleanupGracePeriod,
    CancellationToken hostCancellation,
    TimeProvider timeProvider,
    Action<InvocationRecord>? processExited = null,
    Action<string>? diagnostic = null,
    Action<int>? processStarted = null)
{
    /// <summary>
    /// 子任务并发同步锁。
    /// </summary>
    private readonly object _childTasksLock = new();

    /// <summary>
    /// 活跃的子进程观察与收尾任务集合。受 <see cref="_childTasksLock"/> 保护。
    /// </summary>
    private readonly List<Task> _childTasks = [];

    /// <summary>
    /// 是否已关闭子调用新注册。受 <see cref="_childTasksLock"/> 保护。
    /// </summary>
    private bool _registrationsClosed;

    /// <summary>
    /// 活跃的子调用与观察任务计数。受 <see cref="_childTasksLock"/> 保护。
    /// </summary>
    private int _activeChildTasksCount;

    /// <summary>
    /// 所有子任务完全退出时的排空信号源。
    /// </summary>
    private readonly TaskCompletionSource _allChildTasksDrainedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 校验 ChildStart 报文并登记子调用。
    /// </summary>
    /// <param name="start">ChildStart 报文体，包含调用键、任务标识、Launcher 路径与父调用键。</param>
    /// <returns>子调用生命周期登记句柄。</returns>
    /// <exception cref="ProtocolException">当报文体字段缺失或父调用状态不允许接纳子调用时抛出。</exception>
    public ChildCallRegistration Register(ChildStartBody start)
    {
        lock (_childTasksLock)
        {
            if (_registrationsClosed || _allChildTasksDrainedTcs.Task.IsCompleted)
            {
                throw new ProtocolException("宿主已关闭子调用注册入口。");
            }
            _activeChildTasksCount++;
        }

        try
        {
            if (!IsInvocationKey(start.InvocationKey)
                || string.IsNullOrWhiteSpace(start.TaskId)
                || string.IsNullOrWhiteSpace(start.LauncherPath))
            {
                throw new ProtocolException("ChildStart 缺少有效的调用键、TaskId 或 Launcher 路径。");
            }

            var parent = registry.Get(start.ParentInvocationKey);
            if (!parent.CanAcceptChild || !string.Equals(parent.TaskId, start.TaskId, StringComparison.Ordinal))
            {
                throw new ProtocolException("ChildStart 的父调用已停止，或 TaskId 与父调用不匹配。");
            }

            var record = registry.Register(start.TaskId, start.ParentInvocationKey, start.InvocationKey);
            return new ChildCallRegistration(this, record);
        }
        catch
        {
            ExitChildTaskLifecycle();
            throw;
        }
    }

    /// <summary>
    /// 当子调用未启动（如满载拒绝或调度异常）时放弃登记、标记回收并归还生命周期计数。
    /// </summary>
    /// <param name="record">未启动的子调用记录。</param>
    internal void DismissRecord(InvocationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            record.MarkNonStartedAsReaped();
            registry.TryRemoveReapedTree(record.InvocationKey);
        }
        finally
        {
            record.CompleteLaunch();
            ExitChildTaskLifecycle();
        }
    }

    /// <summary>
    /// 放弃未启动的子调用登记，标记为回收并归还生命周期计数。
    /// </summary>
    /// <param name="record">未启动的权威调用记录。</param>
    public void Dismiss(InvocationRecord record) => DismissRecord(record);

    /// <summary>
    /// 准备产物、启动子进程并挂接退出观察；成功与失败均转换为对应响应信封。
    /// </summary>
    /// <param name="requestId">请求标识符。</param>
    /// <param name="start">ChildStart 报文体。</param>
    /// <param name="record">已登记的权威调用记录。</param>
    /// <returns>ChildStarted 成功响应或 Fault 失败响应信封。</returns>
    public async Task<IpcEnvelope> StartAsync(long requestId, ChildStartBody start, InvocationRecord record)
    {
        var exitObserverStarted = false;
        try
        {
            using var launchTimeout = new CancellationTokenSource(buildTimeout, timeProvider);
            using var launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                hostCancellation,
                record.StopToken,
                launchTimeout.Token);
            var launchToken = launchCancellation.Token;
            launchToken.ThrowIfCancellationRequested();

            var launcher = LauncherPreparation.Prepare(start.LauncherPath);
            launchToken.ThrowIfCancellationRequested();

            if (record.StopRequested)
            {
                await ProcessGuard.ReapExitedAsync(record).ConfigureAwait(false);
                throw new OperationCanceledException("父调用在子 Launcher 启动前已结束。");
            }

            await ProcessGuard.StartAsync(
                record,
                launcher,
                null,
                cleanupGracePeriod,
                cancellationToken: launchToken).ConfigureAwait(false);
            launchToken.ThrowIfCancellationRequested();

            if (record.State == InvocationState.Reaped || record.Process is null)
            {
                throw new OperationCanceledException("子调用在启动交错期间已停止。");
            }

            TrackChildTask(ObserveProcessExitAsync(record));
            exitObserverStarted = true;

            if (record.ProcessId is int processId)
            {
                try
                {
                    processStarted?.Invoke(processId);
                }
                catch (Exception pidEx)
                {
                    await ProcessGuard.StopAsync(record, cleanupGracePeriod).ConfigureAwait(false);
                    throw new InvalidOperationException($"子进程 PID {processId} 登记失败：{pidEx.GetType().Name}", pidEx);
                }
            }

            launchToken.ThrowIfCancellationRequested();

            Report($"ChildStarted invocation={record.InvocationKey} parent={record.ParentKey} pid={record.ProcessId}");

            return ProtocolMessages.CreateChildStarted(
                requestId,
                start.InvocationKey,
                start.TaskId,
                record.Process.Id);
        }
        catch (Exception exception)
        {
            if (!exitObserverStarted && record.Process is not null)
            {
                TrackChildTask(ObserveProcessExitAsync(record));
                exitObserverStarted = true;
            }

            if (record.State == InvocationState.Registered)
            {
                record.MarkStartFailed();
            }

            if (record.State != InvocationState.Reaped)
            {
                try
                {
                    await ProcessGuard.ReapExitedAsync(record).ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    exception = new AggregateException(exception, cleanupException);
                }
            }

            return ProtocolMessages.CreateRequestFault(requestId, "ChildStartFailed", exception.GetType().Name);
        }
        finally
        {
            record.CompleteLaunch();
            ExitChildTaskLifecycle();
        }
    }

    /// <summary>
    /// 停止整棵子调用树并生成真实回收结果及已撤销租约集合。
    /// </summary>
    /// <param name="invocationKey">被停止子树的根调用键。</param>
    /// <param name="subtree">已标记停止的完整子树记录集合。</param>
    /// <returns>真实回收结果与已确认回收的租约集合元组。</returns>
    public async Task<(ChildStopResult Result, IReadOnlyList<string> RevokedLeases)> StopAsync(
        string invocationKey,
        IReadOnlyList<InvocationRecord> subtree)
    {
        var leaseLists = await Task.WhenAll(subtree.Select(record => record.EnsureStoppedAndReapedAsync(cleanupGracePeriod))).ConfigureAwait(false);

        foreach (var record in subtree.Reverse())
        {
            Report($"ChildProcessReaped invocation={record.InvocationKey} pid={record.ProcessId} exit={record.ExitCode} natural={record.ExitedNaturally}");
        }

        var distinctRevoked = leaseLists.SelectMany(l => l).Distinct(StringComparer.Ordinal).ToArray();

        var root = subtree.FirstOrDefault(record => string.Equals(record.InvocationKey, invocationKey, StringComparison.Ordinal)) ?? subtree[0];
        var exitCode = root.ExitCode;
        var allReaped = subtree.All(record => record.State == InvocationState.Reaped);

        if (allReaped)
        {
            registry.TryRemoveReapedTree(invocationKey);
        }

        var result = new ChildStopResult(
            invocationKey,
            exitCode,
            distinctRevoked.Length,
            allReaped ? "Reaped" : "Unresponsive",
            root.ExitedNaturally == true);

        return (result, distinctRevoked);
    }

    /// <summary>
    /// 宿主收尾时无条件并发停止整棵子树并清理已确认退出调用的全部租约。
    /// </summary>
    /// <param name="rootKey">根调用键。</param>
    /// <returns>已确认退出调用的去重租约列表。</returns>
    public async Task<IReadOnlyList<string>> StopAllAsync(string rootKey)
    {
        var subtree = registry.RequestStopSubtree(rootKey);
        var leaseLists = await Task.WhenAll(subtree.Select(record => record.EnsureStoppedAndReapedAsync(cleanupGracePeriod))).ConfigureAwait(false);
        return leaseLists.SelectMany(l => l).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// 处理自然退出的子调用树，并发回收进程并清理其持有的全部租约。
    /// </summary>
    /// <param name="subtree">子树记录列表。</param>
    /// <returns>本次清理掉的去重租约标识列表。</returns>
    public async Task<IReadOnlyList<string>> ReapExitedTreeAsync(IReadOnlyList<InvocationRecord> subtree)
    {
        var leaseLists = await Task.WhenAll(subtree.Select(record => record.EnsureStoppedAndReapedAsync(cleanupGracePeriod))).ConfigureAwait(false);

        foreach (var record in subtree.Reverse())
        {
            Report($"ChildProcessReaped invocation={record.InvocationKey} pid={record.ProcessId} exit={record.ExitCode} natural={record.ExitedNaturally}");
        }

        return leaseLists.SelectMany(l => l).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// 只读方式收集整棵子树当前持有的去重租约列表。
    /// </summary>
    /// <param name="subtree">子树记录列表。</param>
    /// <returns>去重后的租约标识列表。</returns>
    public IReadOnlyList<string> GetLeaseIds(IReadOnlyList<InvocationRecord> subtree)
        => subtree.SelectMany(record => registry.GetLeaseIds(record.InvocationKey))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// 取走指定调用记录关联的全部租约标识。
    /// </summary>
    /// <param name="record">调用记录。</param>
    /// <returns>租约标识列表。</returns>
    private IReadOnlyList<string> TakeCleanedLeaseIds(InvocationRecord record)
        => registry.TakeLeaseIds(record.InvocationKey);

    /// <summary>
    /// 注销一个子调用生命周期计数；当停止接受新注册且活跃计数归零时通知排空完成。
    /// </summary>
    private void ExitChildTaskLifecycle()
    {
        lock (_childTasksLock)
        {
            _activeChildTasksCount--;
            if (_registrationsClosed && _activeChildTasksCount <= 0)
            {
                _allChildTasksDrainedTcs.TrySetResult();
            }
        }
    }

    /// <summary>
    /// 登记并追踪一个子调用关联的异步任务。
    /// </summary>
    /// <param name="task">子调用关联的异步任务。</param>
    private void TrackChildTask(Task task)
    {
        lock (_childTasksLock)
        {
            if (_allChildTasksDrainedTcs.Task.IsCompleted)
            {
                return;
            }
            _childTasks.Add(task);
            _activeChildTasksCount++;
        }

        _ = task.ContinueWith(static (_, state) =>
        {
            var self = (HostChildCalls)state!;
            self.ExitChildTaskLifecycle();
        }, this, TaskScheduler.Default);
    }

    /// <summary>
    /// 等待所有子进程观察与收尾任务完全退出。
    /// 先关闭新注册入口，再等待所有已接受及派生的子任务完全物理退出。
    /// 供 <see cref="HostRunLifetime.Completion"/> 使用，不设超时限制，保证提供方安全释放依赖。
    /// </summary>
    /// <returns>等待所有观察任务完全退出的异步任务。</returns>
    public async Task WaitAllChildTasksAsync()
    {
        Task drainTask;
        lock (_childTasksLock)
        {
            _registrationsClosed = true;
            if (_activeChildTasksCount <= 0)
            {
                return;
            }
            drainTask = _allChildTasksDrainedTcs.Task;
        }

        try
        {
            await drainTask.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 获取当前是否已无活跃子调用任务。
    /// </summary>
    public bool HasNoActiveTasks
    {
        get
        {
            lock (_childTasksLock)
            {
                return _activeChildTasksCount <= 0;
            }
        }
    }

    /// <summary>
    /// 关闭子调用注册入口；若当前已无活跃子调用任务则置位排空完成源。
    /// </summary>
    public void CloseRegistrations()
    {
        lock (_childTasksLock)
        {
            _registrationsClosed = true;
            if (_activeChildTasksCount <= 0)
            {
                _allChildTasksDrainedTcs.TrySetResult();
            }
        }
    }

    /// <summary>
    /// 获取是否所有子任务均已排空退出。
    /// </summary>
    public bool IsAllDrained
    {
        get
        {
            lock (_childTasksLock)
            {
                return _registrationsClosed && _activeChildTasksCount <= 0;
            }
        }
    }

    /// <summary>
    /// 后台异步观察进程物理退出。
    /// </summary>
    /// <param name="record">调用记录。</param>
    /// <returns>观察异步任务。</returns>
    private async Task ObserveProcessExitAsync(InvocationRecord record)
    {
        try
        {
            if (record.ProcessExitTask is null)
            {
                return;
            }

            await record.ProcessExitTask.ConfigureAwait(false);
            await record.WaitForLaunchCompletionAsync().ConfigureAwait(false);
            processExited?.Invoke(record);
        }
        catch (Exception exception)
        {
            Report($"调用 {record.InvocationKey} 的退出护栏收尾失败：{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// 向外部安全分发一条诊断文本。
    /// </summary>
    /// <param name="message">诊断消息。</param>
    private void Report(string message)
    {
        try
        {
            diagnostic?.Invoke(message);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 校验调用键格式（ci_ 前缀 + 32 位十六进制）。
    /// </summary>
    /// <param name="key">调用键。</param>
    /// <returns>格式合法返回 true，否则返回 false。</returns>
    private static bool IsInvocationKey(string key)
        => key.Length == 35
            && key.StartsWith("ci_", StringComparison.Ordinal)
            && key.AsSpan(3).ToString().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// 子调用生命周期登记句柄，负责在调度前持有生命周期计数并在调度失败或放弃时幂等归还。
/// </summary>
internal sealed class ChildCallRegistration : IDisposable
{
    /// <summary>
    /// 所属子调用协调器实例。
    /// </summary>
    private readonly HostChildCalls _owner;

    /// <summary>
    /// 是否已结算生命周期计数（1 为已结算，0 为未结算）。
    /// </summary>
    private int _settled;

    /// <summary>
    /// 初始化子调用登记句柄。
    /// </summary>
    /// <param name="owner">所属子调用协调器。</param>
    /// <param name="record">已登记的权威调用记录。</param>
    internal ChildCallRegistration(HostChildCalls owner, InvocationRecord record)
    {
        _owner = owner;
        Record = record;
    }

    /// <summary>
    /// 获取已登记的权威调用记录。
    /// </summary>
    public InvocationRecord Record { get; }

    /// <summary>
    /// 取走记录交由启动流程托管；启动流程接管后续生命周期计数的归还责任。
    /// </summary>
    /// <returns>权威调用记录。</returns>
    public InvocationRecord TakeForLaunch()
    {
        Interlocked.Exchange(ref _settled, 1);
        return Record;
    }

    /// <summary>
    /// 放弃未启动的子调用登记，标记为回收并归还生命周期计数。
    /// </summary>
    public void Dismiss()
    {
        if (Interlocked.Exchange(ref _settled, 1) != 0)
        {
            return;
        }

        _owner.DismissRecord(Record);
    }

    /// <summary>
    /// 释放登记句柄，若尚未移交启动流程则执行放弃与计数归还。
    /// </summary>
    public void Dispose() => Dismiss();
}
