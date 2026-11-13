using System.Diagnostics;

namespace Ability.Hosting;

/// <summary>
/// 代表宿主权威登记表中记录的单次调用实例。
/// 集中维护调用键、父调用键、任务标识、实际操作系统进程句柄、生命周期状态迁移、租约与受管操作归属。
/// </summary>
internal sealed class InvocationRecord
{
    /// <summary>
    /// 记录内部状态变更与集合维护的同步互斥锁。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// 当前调用持有且尚未释放的资源租约标识集合。
    /// </summary>
    private readonly HashSet<string> _leaseIds = new(StringComparer.Ordinal);

    /// <summary>
    /// 当前调用已清理结算的资源租约标识集合。
    /// </summary>
    private readonly HashSet<string> _cleanedLeaseIds = new(StringComparer.Ordinal);

    /// <summary>
    /// 本调用登记过的受管操作键集合。
    /// </summary>
    private readonly HashSet<string> _operationKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// 进程回收串行闸门，防止取消、子停止与护栏同时回收同一进程产生冲突。
    /// </summary>
    private readonly SemaphoreSlim _reapGate = new(1, 1);

    /// <summary>
    /// 停止取消源，在请求停止时触发。
    /// </summary>
    private readonly CancellationTokenSource _stopCancellation = new();

    /// <summary>
    /// 停止取消源生命周期门控互斥锁。
    /// </summary>
    private readonly object _stopCancellationGate = new();

    /// <summary>
    /// 停止取消源是否已被释放。
    /// </summary>
    private bool _stopCancellationDisposed;

    /// <summary>
    /// 停止取消令牌缓存。
    /// </summary>
    private readonly CancellationToken _stopToken;

    /// <summary>
    /// 当前生命周期状态。
    /// </summary>
    private InvocationState _state = InvocationState.Registered;

    /// <summary>
    /// 是否已发出停止请求。
    /// </summary>
    private bool _stopRequested;

    /// <summary>
    /// 启动结果落定完成源（绑定进程、启动失败或未启动即被停止时触发）。
    /// </summary>
    private readonly TaskCompletionSource _startResolved = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 建启流程整体收口完成源（涵盖成功、失败与取消），停止路径据此确认不会再产生新进程。
    /// </summary>
    private readonly TaskCompletionSource _launchCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 初始化调用权威记录。
    /// </summary>
    /// <param name="taskId">所属任务唯一标识符。</param>
    /// <param name="parentKey">父调用唯一标识键；根调用为 null。</param>
    /// <param name="invocationKey">本调用唯一标识键（同时作为 IPC 管道名称）。</param>
    public InvocationRecord(string taskId, string? parentKey, string invocationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationKey);

        TaskId = taskId;
        ParentKey = parentKey;
        InvocationKey = invocationKey;
        _stopToken = _stopCancellation.Token;
    }

    /// <summary>所属任务唯一标识符。</summary>
    public string TaskId { get; }

    /// <summary>父调用唯一标识键；根调用为 null。</summary>
    public string? ParentKey { get; }

    /// <summary>本调用唯一标识键。</summary>
    public string InvocationKey { get; }

    /// <summary>底层操作系统进程实例；启动成功后绑定。</summary>
    public Process? Process { get; private set; }

    /// <summary>底层进程 ID；启动成功后绑定。</summary>
    public int? ProcessId { get; private set; }

    /// <summary>标准输出有界读取异步任务。</summary>
    public Task<string>? StandardOutputTask { get; private set; }

    /// <summary>标准错误输出有界读取异步任务。</summary>
    public Task<string>? StandardErrorTask { get; private set; }

    /// <summary>底层进程退出等待任务。</summary>
    public Task? ProcessExitTask { get; private set; }

    /// <summary>进程最终退出码；未确认退出时为 null。</summary>
    public int? ExitCode { get; private set; }

    /// <summary>进程是否在宽限期内自行自然退出；若为 false 则表示被强制终止。</summary>
    public bool? ExitedNaturally { get; private set; }

    /// <summary>获取当前生命周期状态。</summary>
    public InvocationState State { get { lock (_gate) return _state; } }

    /// <summary>获取当前活动租约标识列表快照。</summary>
    public IReadOnlyList<string> LeaseIds { get { lock (_gate) return _leaseIds.ToArray(); } }

    /// <summary>获取本调用登记过的受管操作键列表快照。</summary>
    public IReadOnlyList<string> OperationKeys { get { lock (_gate) return _operationKeys.ToArray(); } }

    /// <summary>是否已收到停止请求。</summary>
    public bool StopRequested { get { lock (_gate) return _stopRequested; } }

    /// <summary>根调用持有的任务专属临时目录收尾审计报告。</summary>
    public TaskTempReport? TaskTempReport { get; set; }

    /// <summary>本调用关联的 stderr 结构化诊断收集器。</summary>
    public HostTraceCollector TraceCollector { get; } = new();

    /// <summary>回收串行并发闸门。</summary>
    public SemaphoreSlim ReapGate => _reapGate;

    /// <summary>停止取消令牌；用于中断启动或决策等待。</summary>
    public CancellationToken StopToken => _stopToken;

    /// <summary>
    /// 是否仍能接纳新的子调用：未收到停止请求且处于 Registered、Starting 或 Running 状态。
    /// </summary>
    public bool CanAcceptChild
    {
        get
        {
            lock (_gate)
            {
                return !_stopRequested && _state is InvocationState.Registered or InvocationState.Starting or InvocationState.Running;
            }
        }
    }

    /// <summary>等待启动结果落定（绑定进程、失败或未启动即停止）。</summary>
    public Task WaitForStartResolutionAsync() => _startResolved.Task;

    /// <summary>等待建启流程整体收口。</summary>
    public Task WaitForLaunchCompletionAsync() => _launchCompleted.Task;

    /// <summary>标记建启流程收口结束，唤醒等待建启完成的停止路径。</summary>
    public void CompleteLaunch()
    {
        _launchCompleted.TrySetResult();
        ReleaseStopCancellationAfterLifecycle();
    }

    /// <summary>推进至 Starting 启动准备状态。</summary>
    public void MarkStarting()
    {
        lock (_gate)
        {
            if (_state != InvocationState.Registered)
            {
                throw new InvalidOperationException($"调用不能从当前状态 {_state} 推进到 Starting。");
            }

            _state = InvocationState.Starting;
        }
    }

    /// <summary>调用尚未绑定进程即被停止时收口。</summary>
    public void MarkNotStarted()
    {
        lock (_gate)
        {
            if (Process is not null || _state is not (InvocationState.Registered or InvocationState.StopRequested))
            {
                throw new InvalidOperationException("只有未绑定进程且未进入终态的调用才能标记为 NotStarted。");
            }

            _state = InvocationState.Exited;
            ExitCode = null;
            _startResolved.TrySetResult();
        }
    }

    /// <summary>推进至启动失败终态。</summary>
    public void MarkStartFailed()
    {
        lock (_gate)
        {
            if (Process is not null || _state is not (InvocationState.Registered or InvocationState.Starting or InvocationState.StopRequested))
            {
                throw new InvalidOperationException("当前状态不可标记为启动失败。");
            }

            _state = InvocationState.Exited;
            ExitCode = null;
            _startResolved.TrySetResult();
        }
    }

    /// <summary>
    /// 将尚未启动物理进程的记录安全、幂等地推进至已回收终态。
    /// 无论当前处于 Registered、Starting 还是 Exited，只要未绑定物理进程即可推进并解除启动等待。
    /// </summary>
    public void MarkNonStartedAsReaped()
    {
        lock (_gate)
        {
            if (Process is not null)
            {
                throw new InvalidOperationException("已绑定物理进程的调用不能按未启动记录直接回收。");
            }

            if (_state != InvocationState.Reaped)
            {
                _state = InvocationState.Reaped;
                ExitCode = null;
                ExitedNaturally = false;
                _startResolved.TrySetResult();
            }
        }
    }

    /// <summary>
    /// 获取当前已收集的标准错误文本快照的委托。
    /// </summary>
    private Func<string>? _stderrSnapshotProvider;

    /// <summary>
    /// 获取当前已读取到的标准错误诊断快照。
    /// </summary>
    /// <returns>已收集的 stderr 文本，未配置或空时返回 string.Empty。</returns>
    public string GetStderrSnapshot() => _stderrSnapshotProvider?.Invoke() ?? string.Empty;

    /// <summary>
    /// 将启动成功的实际进程及输出读取任务绑定到当前记录。
    /// </summary>
    /// <param name="process">已启动的进程对象。</param>
    /// <param name="stdout">标准输出读取任务。</param>
    /// <param name="stderr">标准错误输出读取任务。</param>
    /// <param name="stderrSnapshotProvider">可选的标准错误文本快照获取器。</param>
    /// <returns>若绑定前已请求停止返回 true，提示调用方必须立即回收该进程；否则返回 false。</returns>
    public bool AttachProcess(Process process, Task<string> stdout, Task<string> stderr, Func<string>? stderrSnapshotProvider = null)
    {
        ArgumentNullException.ThrowIfNull(process);

        lock (_gate)
        {
            if (_state != InvocationState.Starting)
            {
                throw new InvalidOperationException("只能向处于 Starting 状态的记录绑定实际进程。");
            }

            Process = process;
            ProcessId = process.Id;
            StandardOutputTask = stdout;
            StandardErrorTask = stderr;
            _stderrSnapshotProvider = stderrSnapshotProvider;
            ProcessExitTask = process.WaitForExitAsync();
            _state = _stopRequested ? InvocationState.StopRequested : InvocationState.Running;
            _startResolved.TrySetResult();
            return _stopRequested;
        }
    }

    /// <summary>
    /// 请求停止本调用；具备幂等性。
    /// </summary>
    /// <returns>若需要调用方继续等待并回收进程返回 true；若该调用已结束或已回收返回 false。</returns>
    public bool RequestStop()
    {
        var shouldCancel = false;
        var shouldReturn = true;

        lock (_gate)
        {
            _stopRequested = true;
            if (_state is InvocationState.Reaped or InvocationState.Exited)
            {
                shouldReturn = false;
            }
            else if (_state == InvocationState.Registered)
            {
                _state = InvocationState.Exited;
                ExitCode = null;
                _startResolved.TrySetResult();
                shouldCancel = true;
            }
            else if (_state != InvocationState.Starting)
            {
                _state = InvocationState.StopRequested;
                shouldCancel = true;
            }
            else
            {
                shouldCancel = true;
            }
        }

        if (shouldCancel)
        {
            lock (_stopCancellationGate)
            {
                if (!_stopCancellationDisposed)
                {
                    try { _stopCancellation.Cancel(); } catch { }
                }
            }
        }

        return shouldReturn;
    }

    /// <summary>
    /// 确认实际进程已物理退出并记录其退出码。
    /// </summary>
    public void MarkExited()
    {
        lock (_gate)
        {
            if (Process is not null && !Process.HasExited)
            {
                throw new InvalidOperationException("尚未确认实际进程退出，拒绝推进到 Exited 状态。");
            }

            ExitCode = Process?.ExitCode;
            _state = InvocationState.Exited;
        }
    }

    /// <summary>
    /// 确认调用关联资源已回收完成，推进到 Reaped 终态。
    /// </summary>
    /// <param name="exitedNaturally">是否自然退出。</param>
    public void MarkReaped(bool exitedNaturally = true)
    {
        lock (_gate)
        {
            if (_state != InvocationState.Exited)
            {
                throw new InvalidOperationException("只有已确认 Exited 的调用才能推进到 Reaped。");
            }

            _state = InvocationState.Reaped;
            ExitedNaturally = exitedNaturally;
        }
    }

    /// <summary>
    /// 在建启流程结束且记录已 Reaped 后释放停止取消源。
    /// </summary>
    public void ReleaseStopCancellationAfterLifecycle()
    {
        if (!_launchCompleted.Task.IsCompleted || State != InvocationState.Reaped)
        {
            return;
        }

        lock (_stopCancellationGate)
        {
            if (!_stopCancellationDisposed)
            {
                _stopCancellationDisposed = true;
                _stopCancellation.Dispose();
            }
        }
    }

    /// <summary>
    /// 登记由本调用持有的资源租约。
    /// </summary>
    /// <param name="leaseId">租约全局唯一标识符。</param>
    public void TrackLease(string leaseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);

        lock (_gate)
        {
            if (_stopRequested || _state is InvocationState.Exited or InvocationState.Reaped)
            {
                throw new InvalidOperationException("已停止或已退出的调用不能再取得新租约。");
            }

            _leaseIds.Add(leaseId);
        }
    }

    /// <summary>
    /// 释放由本调用持有的租约。
    /// </summary>
    /// <param name="leaseId">租约标识符。</param>
    /// <returns>若成功命中并移除返回 true；否则返回 false。</returns>
    public bool ReleaseLease(string leaseId)
    {
        lock (_gate)
        {
            return _leaseIds.Remove(leaseId);
        }
    }

    /// <summary>
    /// 取走本调用的全部租约（包含未释放与已清理的合并去重集合）并清空记录。
    /// 仅在调用已确认退出终态（Reaped）时才允许取走并清空；若未确认退出则返回空列表并保留租约。
    /// </summary>
    /// <returns>已清理的租约列表。</returns>
    public IReadOnlyList<string> TakeLeaseIds()
    {
        lock (_gate)
        {
            if (_state != InvocationState.Reaped)
            {
                return [];
            }

            var leases = _leaseIds.Concat(_cleanedLeaseIds).Distinct(StringComparer.Ordinal).ToArray();
            _leaseIds.Clear();
            _cleanedLeaseIds.Clear();
            return leases;
        }
    }

    /// <summary>
    /// 获取当前调用关联的全部租约只读副本（不清空集合）。
    /// </summary>
    /// <returns>租约列表。</returns>
    public IReadOnlyList<string> GetLeaseIds()
    {
        lock (_gate)
        {
            return _leaseIds.Concat(_cleanedLeaseIds).Distinct(StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>
    /// 将剩余的活动租约并入已清理租约集合并清空活动租约表。
    /// </summary>
    /// <returns>累计已清理租约数组。</returns>
    public IReadOnlyList<string> CleanLeaseIds()
    {
        lock (_gate)
        {
            _cleanedLeaseIds.UnionWith(_leaseIds);
            _leaseIds.Clear();
            return _cleanedLeaseIds.ToArray();
        }
    }

    /// <summary>
    /// 登记本调用创建的受管操作键。
    /// </summary>
    /// <param name="operationKey">操作键标识。</param>
    public void TrackOperation(string operationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);

        lock (_gate)
        {
            _operationKeys.Add(operationKey);
        }
    }

    /// <summary>
    /// 回滚撤销本调用创建的受管操作键。
    /// </summary>
    /// <param name="operationKey">操作键标识。</param>
    public void UntrackOperation(string operationKey)
    {
        if (string.IsNullOrWhiteSpace(operationKey))
        {
            return;
        }

        lock (_gate)
        {
            _operationKeys.Remove(operationKey);
        }
    }

    /// <summary>
    /// 正在执行中的停止与回收任务引用。
    /// </summary>
    private Task<IReadOnlyList<string>>? _activeStopReapTask;

    /// <summary>
    /// 成功推进到 Reaped 终态后永久缓存的停止与回收任务结果。
    /// </summary>
    private Task<IReadOnlyList<string>>? _completedStopReapTask;

    /// <summary>
    /// 停止与回收单例任务互斥同步锁。
    /// </summary>
    private readonly object _stopReapLock = new();

    /// <summary>
    /// 确保本调用的底层进程被安全停止并原子获取清理后的租约标识列表。
    /// 并发调用此方法将共享同一个底层任务并复用完全相同的回收结果，防止租约竞争丢失或重复撤销。
    /// 仅当调用确认达到 Reaped 终态时才永久缓存结果；强杀未确认退出时不缓存，允许迟到自然退出继续完成回收。
    /// </summary>
    /// <param name="cleanupGracePeriod">进程退出等待宽限期。</param>
    /// <returns>已清理的去重租约列表任务。</returns>
    public Task<IReadOnlyList<string>> EnsureStoppedAndReapedAsync(TimeSpan cleanupGracePeriod)
    {
        lock (_stopReapLock)
        {
            if (_state == InvocationState.Reaped && _completedStopReapTask is not null)
            {
                return _completedStopReapTask;
            }

            if (_activeStopReapTask is not null && !_activeStopReapTask.IsCompleted)
            {
                return _activeStopReapTask;
            }

            _activeStopReapTask = PerformStopAndReapAsync(cleanupGracePeriod);
            return _activeStopReapTask;
        }
    }

    /// <summary>
    /// 执行实际的停止与租约收集流程。
    /// </summary>
    private async Task<IReadOnlyList<string>> PerformStopAndReapAsync(TimeSpan cleanupGracePeriod)
    {
        try
        {
            await WaitForLaunchCompletionAsync().ConfigureAwait(false);
            await ProcessGuard.StopAsync(this, cleanupGracePeriod).ConfigureAwait(false);
            var leases = TakeLeaseIds();
            lock (_stopReapLock)
            {
                if (_state == InvocationState.Reaped)
                {
                    _completedStopReapTask = Task.FromResult(leases);
                }
            }
            return leases;
        }
        finally
        {
            lock (_stopReapLock)
            {
                if (_state != InvocationState.Reaped)
                {
                    _activeStopReapTask = null;
                }
            }
        }
    }
}
