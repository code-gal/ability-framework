using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// 负责宿主端治理能力的状态汇总与业务协调（内部支持模型与上下文声明分部）。
/// </summary>
internal sealed partial class HostGovernanceCoordinator
{
    /// <summary>
    /// 治理未决请求的生命周期上下文记录。
    /// 集中管理双令牌生命周期（回调专用的 CallbackCts 与请求超时计时的 RequestTimeoutCts）。
    /// </summary>
    private sealed class GovernancePendingRequest
    {
        /// <summary>
        /// 请求全局唯一标识编号。
        /// </summary>
        public long RequestId { get; }

        /// <summary>
        /// 请求分类标识。
        /// </summary>
        public string RequestType { get; }

        /// <summary>
        /// 发起请求的 IPC 双工通信连接。
        /// </summary>
        public IpcConnection Connection { get; }

        /// <summary>
        /// 回调专用的取消令牌源（超时或请求结束时取消）。
        /// </summary>
        public CancellationTokenSource CallbackCts { get; }

        /// <summary>
        /// 请求生命周期超时计时专用的取消令牌源。
        /// </summary>
        public CancellationTokenSource RequestTimeoutCts { get; }

        /// <summary>
        /// 请求生命周期互斥锁，统一协调初始化、使用者登记、结束裁决与最终释放。
        /// </summary>
        private readonly object _gate = new();

        /// <summary>
        /// 活跃执行工作单元计数（决策回调、超时任务及异步取消任务）。
        /// 采用动态登记，严禁预设固定数值。
        /// </summary>
        private int _activeWorkers;

        /// <summary>
        /// 完成裁决标记（表示已通过正常结果、超时、异常或中止裁决完成）。
        /// </summary>
        private bool _completed;

        /// <summary>
        /// 是否已完成初始化标记。
        /// </summary>
        private bool _initialized;

        /// <summary>
        /// 取消源资源是否已完成安全释放。
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// 所属治理协调器引用。
        /// </summary>
        private readonly HostGovernanceCoordinator _coordinator;

        /// <summary>
        /// 初始化治理未决请求记录。
        /// </summary>
        /// <param name="coordinator">所属治理协调器实例。</param>
        /// <param name="requestId">请求标识编号。</param>
        /// <param name="requestType">请求类型名称。</param>
        /// <param name="connection">通信连接实例。</param>
        /// <param name="callbackCts">回调取消令牌源。</param>
        /// <param name="requestTimeoutCts">超时计时取消令牌源。</param>
        public GovernancePendingRequest(
            HostGovernanceCoordinator coordinator,
            long requestId,
            string requestType,
            IpcConnection connection,
            CancellationTokenSource callbackCts,
            CancellationTokenSource requestTimeoutCts)
        {
            _coordinator = coordinator;
            RequestId = requestId;
            RequestType = requestType;
            Connection = connection;
            CallbackCts = callbackCts;
            RequestTimeoutCts = requestTimeoutCts;
            _activeWorkers = 0;
            _completed = false;
            _initialized = false;
            _disposed = false;
        }

        /// <summary>
        /// 尝试登记一个即将启动的实际后台工作单元（如决策任务、超时定时任务）。
        /// 若请求已处于完成或已释放状态，或协调器已关闭，则拒绝登记并返回 false。
        /// </summary>
        /// <returns>若成功登记返回 true；若已被裁决完成或已关闭释放返回 false。</returns>
        public bool TryRegisterWorker()
        {
            lock (_gate)
            {
                if (_completed || _disposed)
                {
                    return false;
                }

                if (!_coordinator.TryEnterCoordinatorWorker())
                {
                    return false;
                }

                _activeWorkers++;
                return true;
            }
        }

        /// <summary>
        /// 标记请求初始化阶段结束。
        /// 若在初始化期间已被提前裁决结束且工作者全部退出，立即安全释放取消源。
        /// </summary>
        public void MarkInitializationComplete()
        {
            lock (_gate)
            {
                _initialized = true;
                CheckDisposeUnderLock();
            }
        }

        /// <summary>
        /// 当一个后台工作单元退出时调用。
        /// 先在锁内安全处理取消源释放，随后才归还协调器工作者计数，杜绝排空信号提前触发。
        /// </summary>
        public void OnWorkerExit()
        {
            lock (_gate)
            {
                _activeWorkers--;
                CheckDisposeUnderLock();
            }

            _coordinator.ExitCoordinatorWorker();
        }

        /// <summary>
        /// 原子尝试标记请求完成。
        /// 若成功置位，主动取消超时计时以唤醒并终止超时任务。
        /// </summary>
        /// <returns>若先前未完成并成功置位返回 true；否则返回 false。</returns>
        public bool TryMarkCompleted()
        {
            lock (_gate)
            {
                if (_completed)
                {
                    return false;
                }

                _completed = true;
                try { RequestTimeoutCts.Cancel(); } catch { }
                CheckDisposeUnderLock();
                return true;
            }
        }

        /// <summary>
        /// 尝试触发请求超时。
        /// 原子置位完成并取消超时计时（立即解除槽位等待），将异步取消执行纳入工作者所有权以防外部阻塞及提前释放。
        /// </summary>
        /// <returns>若先前未完成且成功置位超时返回 true；否则返回 false。</returns>
        public bool TryTriggerTimeout()
        {
            lock (_gate)
            {
                if (_completed)
                {
                    return false;
                }

                _completed = true;
                try { RequestTimeoutCts.Cancel(); } catch { }
                if (_coordinator.TryEnterCoordinatorWorker(isDerivedCleanup: true))
                {
                    _activeWorkers++;
                }
                else
                {
                    CheckDisposeUnderLock();
                    return true;
                }
            }

            _ = Task.Run(() =>
            {
                try
                {
                    try { CallbackCts.Cancel(); } catch { }
                }
                finally
                {
                    OnWorkerExit();
                }
            });

            return true;
        }

        /// <summary>
        /// 宿主关闭时中止当前未决请求。
        /// 将异步取消任务纳入工作者所有权，工作单元退出后再释放取消源。
        /// </summary>
        public void Abort()
        {
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                try { RequestTimeoutCts.Cancel(); } catch { }
                if (_coordinator.TryEnterCoordinatorWorker(isDerivedCleanup: true))
                {
                    _activeWorkers++;
                }
                else
                {
                    CheckDisposeUnderLock();
                    return;
                }
            }

            _ = Task.Run(() =>
            {
                try
                {
                    try { CallbackCts.Cancel(); } catch { }
                }
                finally
                {
                    OnWorkerExit();
                }
            });
        }

        /// <summary>
        /// 检查当前请求是否已被裁决结束。
        /// </summary>
        public bool IsCompleted
        {
            get
            {
                lock (_gate)
                {
                    return _completed;
                }
            }
        }

        /// <summary>
        /// 在持有 _gate 锁的条件下检查是否满足安全释放取消源条件。
        /// 必须同时满足：初始化完成、已被裁决结束、所有工作单元均物理退出且尚未释放。
        /// </summary>
        private void CheckDisposeUnderLock()
        {
            if (_initialized && _completed && _activeWorkers == 0 && !_disposed)
            {
                _disposed = true;
                try { CallbackCts.Dispose(); }
                catch (Exception ex) { _coordinator.RecordReleaseIssue("RequestCallbackCtsDispose", ex); }
                try { RequestTimeoutCts.Dispose(); }
                catch (Exception ex) { _coordinator.RecordReleaseIssue("RequestTimeoutCtsDispose", ex); }
            }
        }
    }

    /// <summary>
    /// 动态运行策略未决更新对账记录。
    /// 包含单次原子完成裁决，防止确认、拒绝与超时交错胜出。
    /// </summary>
    private sealed class PendingPolicyUpdate
    {
        /// <summary>
        /// 单次完成裁决原子标记（0 未裁决，1 已裁决完成）。
        /// </summary>
        private int _completed;

        /// <summary>
        /// 更新请求分配的唯一编号。
        /// </summary>
        public long RequestId { get; }

        /// <summary>
        /// 受管操作唯一标识键。
        /// </summary>
        public string OperationKey { get; }

        /// <summary>
        /// 新策略版本号。
        /// </summary>
        public long Version { get; }

        /// <summary>
        /// 策略来源。
        /// </summary>
        public string Source { get; }

        /// <summary>
        /// 生效的目标策略。
        /// </summary>
        public RunPolicyBody TargetPolicy { get; }

        /// <summary>
        /// 确认等待超时计时取消令牌源。
        /// </summary>
        public CancellationTokenSource TimeoutCts { get; }

        /// <summary>
        /// 初始化策略未决更新记录。
        /// </summary>
        public PendingPolicyUpdate(
            long requestId,
            string operationKey,
            long version,
            string source,
            RunPolicyBody targetPolicy,
            CancellationTokenSource timeoutCts)
        {
            RequestId = requestId;
            OperationKey = operationKey;
            Version = version;
            Source = source;
            TargetPolicy = targetPolicy;
            TimeoutCts = timeoutCts;
            _completed = 0;
        }

        /// <summary>
        /// 尝试赢得单次完成裁决。
        /// 仅首个到达的路径（确认、拒绝或超时）返回 true。
        /// </summary>
        /// <returns>若成功赢得裁决返回 true；若已被其他路径裁决返回 false。</returns>
        public bool TryComplete() => Interlocked.CompareExchange(ref _completed, 1, 0) == 0;

        /// <summary>
        /// 触发更新确认超时取消，使后台超时任务提前退出。
        /// </summary>
        public void CancelTimeout()
        {
            try { TimeoutCts.Cancel(); } catch { }
        }
    }

    /// <summary>
    /// 协调器是否接受新工作登记。受 <see cref="_stateLock"/> 保护。
    /// </summary>
    private bool _acceptingNewWork = true;

    /// <summary>
    /// 活跃的协调器后台工作单元总数（包括治理未决请求工作者、策略更新超时、答复交付任务及派生清理）。受 <see cref="_stateLock"/> 保护。
    /// </summary>
    private int _activeCoordinatorWorkers;

    /// <summary>
    /// 当协调器进入关闭状态且所有工作单元完全退出时的通知源。
    /// </summary>
    private readonly TaskCompletionSource _allWorkersDrainedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 尝试登记一个协调器级的活跃后台工作单元。
    /// </summary>
    /// <param name="isDerivedCleanup">是否属于已有工作的派生清理（派生清理即使已关闭也允许进入以保证资源收敛）。</param>
    /// <returns>若成功登记返回 true；若已关闭且不允许新工作返回 false。</returns>
    private bool TryEnterCoordinatorWorker(bool isDerivedCleanup = false)
    {
        lock (_stateLock)
        {
            if (!_acceptingNewWork)
            {
                if (!isDerivedCleanup || _allWorkersDrainedTcs.Task.IsCompleted)
                {
                    return false;
                }
            }

            _activeCoordinatorWorkers++;
            return true;
        }
    }

    /// <summary>
    /// 注销一个协调器级的活跃后台工作单元；当停止接受新工作且活跃计数归零时通知排空完成。
    /// </summary>
    private void ExitCoordinatorWorker()
    {
        lock (_stateLock)
        {
            _activeCoordinatorWorkers--;
            if (!_acceptingNewWork && _activeCoordinatorWorkers <= 0)
            {
                _allWorkersDrainedTcs.TrySetResult();
            }
        }
    }

    /// <summary>
    /// 治理模块内部捕获的工作单元异常与交付故障队列。
    /// </summary>
    private readonly ConcurrentQueue<HostCleanupIssue> _governanceIssues = new();

    /// <summary>
    /// 记录治理协调器内部捕获的收尾问题或交付异常。
    /// </summary>
    /// <param name="stage">发生阶段名称。</param>
    /// <param name="failure">引发异常的对象。</param>
    public void RecordIssue(string stage, Exception failure)
    {
        _governanceIssues.Enqueue(new HostCleanupIssue(stage, failure.GetType().Name, DescribeSafeError(failure)));
    }

    /// <summary>
    /// 获取治理协调器生命周期内捕获的所有问题列表快照。
    /// </summary>
    /// <returns>收尾问题列表快照。</returns>
    public IReadOnlyList<HostCleanupIssue> GetCapturedIssues() => _governanceIssues.ToArray();

    /// <summary>释放阶段是否发生过异常，由宿主收尾按事实读取。</summary>
    private volatile bool _releaseFailed;

    /// <summary>获取治理组件释放阶段是否发生过异常。</summary>
    public bool HasReleaseFailure => _releaseFailed;

    /// <summary>
    /// 记录释放阶段异常并置位释放失败事实；问题仍进入治理问题队列，事实供宿主完成判定。
    /// </summary>
    /// <param name="stage">发生阶段名称。</param>
    /// <param name="failure">释放阶段异常。</param>
    public void RecordReleaseIssue(string stage, Exception failure)
    {
        _releaseFailed = true;
        RecordIssue(stage, failure);
    }

    /// <summary>
    /// 在锁内预先登记活跃工作者后启动异步答复写出任务；若已关闭则放弃启动。
    /// </summary>
    /// <param name="deliveryFactory">创建写出答复异步任务的工厂委托。</param>
    private void StartDeliveryWorker(Func<Task> deliveryFactory)
    {
        if (!TryEnterCoordinatorWorker())
        {
            return;
        }

        try
        {
            var deliveryTask = deliveryFactory();
            _ = deliveryTask.ContinueWith(static (t, state) =>
            {
                var coordinator = (HostGovernanceCoordinator)state!;
                try
                {
                    if (t.IsFaulted && t.Exception is not null)
                    {
                        coordinator.RecordIssue("GovernanceDelivery", t.Exception.GetBaseException());
                    }
                }
                finally
                {
                    coordinator.ExitCoordinatorWorker();
                }
            }, this, TaskScheduler.Default);
        }
        catch
        {
            ExitCoordinatorWorker();
            throw;
        }
    }

    /// <summary>
    /// 获取协调器是否已完成所有工作者排空。
    /// </summary>
    public bool IsDrained
    {
        get
        {
            lock (_stateLock)
            {
                return !_acceptingNewWork && _activeCoordinatorWorkers <= 0;
            }
        }
    }

    /// <summary>
    /// 等待所有治理决策回调、超时计时与中止清理工作者完全物理退出。
    /// 供 <see cref="HostRunLifetime.Completion"/> 使用，不设超时限制，保证提供方安全释放依赖。
    /// </summary>
    /// <returns>等待完全退出的异步任务。</returns>
    public async Task WaitAllDrainedAsync()
    {
        AbortPendingRequests();
        Task drainTask;
        lock (_stateLock)
        {
            if (_activeCoordinatorWorkers <= 0)
            {
                return;
            }
            drainTask = _allWorkersDrainedTcs.Task;
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
    /// 获取当前全部受管操作的不可变最终快照列表。
    /// </summary>
    /// <returns>受管操作快照列表。</returns>
    public IReadOnlyList<OperationSnapshot> GetOperationSnapshots()
        => _operations.Values.Select(static op => op.Snapshot()).ToList();

    /// <summary>
    /// 尝试根据键获取对应的受管操作视图。
    /// </summary>
    /// <param name="operationKey">操作唯一标识键。</param>
    /// <param name="operation">成功匹配时输出的操作实例。</param>
    /// <returns>若存在返回 true；否则返回 false。</returns>
    public bool TryGetOperation(string operationKey, out HostOperation? operation)
        => _operations.TryGetValue(operationKey, out operation);

    /// <summary>
    /// 取消所有未决等待请求并清空对账表，将协调器置位为已关闭且不再接纳新工作。
    /// </summary>
    public void AbortPendingRequests()
    {
        lock (_stateLock)
        {
            _acceptingNewWork = false;
            _isClosed = true;
        }

        foreach (var pending in _pendingRequests.Values)
        {
            try { pending.Abort(); } catch { }
        }

        foreach (var update in _pendingPolicyUpdates.Values)
        {
            try { update.TryComplete(); update.CancelTimeout(); } catch { }
        }

        _pendingRequests.Clear();
        _pendingPolicyUpdates.Clear();

        lock (_stateLock)
        {
            if (_activeCoordinatorWorkers <= 0)
            {
                _allWorkersDrainedTcs.TrySetResult();
            }
        }
    }

    /// <summary>
    /// 获取指定请求编号对应的未决策略更新操作键。
    /// </summary>
    /// <param name="requestId">策略更新请求编号。</param>
    /// <param name="operationKey">若存在返回目标操作键，否则为 null。</param>
    /// <returns>若存在匹配的未决更新返回 true；否则返回 false。</returns>
    public bool TryGetPendingPolicyUpdateOperationKey(long requestId, out string? operationKey)
    {
        if (_pendingPolicyUpdates.TryGetValue(requestId, out var pendingUpdate))
        {
            operationKey = pendingUpdate.OperationKey;
            return true;
        }

        operationKey = null;
        return false;
    }

    /// <summary>
    /// 检查指定操作是否存在对应目标版本的待确认策略更新。
    /// </summary>
    /// <param name="operationKey">受管操作唯一标识键。</param>
    /// <param name="version">策略版本号。</param>
    /// <returns>若存在匹配的未决更新返回 true；否则返回 false。</returns>
    public bool HasPendingPolicyUpdate(string operationKey, long version)
    {
        foreach (var pending in _pendingPolicyUpdates.Values)
        {
            if (string.Equals(pending.OperationKey, operationKey, StringComparison.Ordinal) && pending.Version == version)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 尝试查询指定操作和版本的未决策略更新元数据（来源与预期到期动作）。
    /// </summary>
    /// <param name="operationKey">受管操作唯一标识键。</param>
    /// <param name="version">策略版本号。</param>
    /// <param name="info">成功时输出策略来源与预期到期动作。</param>
    /// <returns>若存在匹配的未决更新返回 true；否则返回 false。</returns>
    public bool TryGetPendingPolicyUpdate(string operationKey, long version, out (string Source, string DeadlineAction)? info)
    {
        info = null;
        foreach (var pending in _pendingPolicyUpdates.Values)
        {
            if (string.Equals(pending.OperationKey, operationKey, StringComparison.Ordinal) && pending.Version == version)
            {
                info = (pending.Source, pending.TargetPolicy.DeadlineAction);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 治理模块内部源生成 JSON 序列化上下文。
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(string))]
    private sealed partial class HostGovernanceJsonContext : JsonSerializerContext;
}
