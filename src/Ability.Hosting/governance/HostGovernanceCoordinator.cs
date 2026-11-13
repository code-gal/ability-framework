using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// 负责宿主端治理能力（资源租约、参数补充、数据源恢复及受管操作）的状态汇总与业务协调。
/// 管理活动租约记录、操作树层级与生命周期收口，并在独立受控的任务中调度调用方决策回调与 Schema 模式核验。
/// </summary>
internal sealed partial class HostGovernanceCoordinator
{
    /// <summary>
    /// 状态同步锁，原子协调任务关闭、批准登记与租约清空。
    /// </summary>
    private readonly object _stateLock = new();

    /// <summary>
    /// 当前处于已批准活跃状态的资源租约字典（键为 leaseId）。受 <see cref="_stateLock"/> 保护。
    /// </summary>
    private readonly Dictionary<string, LeaseRequestBody> _activeLeases = new(StringComparer.Ordinal);

    /// <summary>
    /// 当前治理协调器寿命内已成功签发的租约编号。受 <see cref="_stateLock"/> 保护，释放后仍保留以阻止旧通知命中新授权。
    /// </summary>
    private readonly HashSet<string> _issuedLeaseIds = new(StringComparer.Ordinal);

    /// <summary>
    /// 已收到释放通知的历史租约编号集合。
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _releasedLeases = new(StringComparer.Ordinal);

    /// <summary>
    /// 释放记录的最大缓存容量上限。
    /// </summary>
    private const int MaxReleasedLeasesCount = 256;

    /// <summary>
    /// 当前已登记的全部受管操作集合（键为 operationKey）。
    /// </summary>
    private readonly ConcurrentDictionary<string, HostOperation> _operations = new(StringComparer.Ordinal);

    /// <summary>
    /// 未决治理请求最大容量上限。
    /// </summary>
    private const int MaxPendingRequests = 256;

    /// <summary>
    /// 补参答复载荷允许的最大字节数（有界编码校验上限）。
    /// </summary>
    private const int MaxParamPayloadBytes = 1024 * 1024;

    /// <summary>
    /// 治理决策并发执行容量门控信号量（限制外部回调占用的最大执行任务数）。
    /// </summary>
    private readonly SemaphoreSlim _decisionExecutionGate = new(64, 64);

    /// <summary>
    /// 协调器是否已处于关闭状态标记。受 <see cref="_stateLock"/> 保护。
    /// </summary>
    private bool _isClosed;

    /// <summary>
    /// 处于等待决策中的未决请求状态字典（键为 requestId）。
    /// </summary>
    private readonly ConcurrentDictionary<long, GovernancePendingRequest> _pendingRequests = new();

    /// <summary>
    /// 动态策略更新未决对账记录字典（键为 requestId）。
    /// </summary>
    private readonly ConcurrentDictionary<long, PendingPolicyUpdate> _pendingPolicyUpdates = new();

    /// <summary>
    /// 诊断消息通知回调委托。
    /// </summary>
    private readonly Action<string>? _diagnosticCallback;

    /// <summary>
    /// 初始化宿主端治理协调器实例。
    /// </summary>
    /// <param name="diagnosticCallback">可选的诊断日志分发回调委托。</param>
    public HostGovernanceCoordinator(Action<string>? diagnosticCallback = null)
    {
        _diagnosticCallback = diagnosticCallback;
    }

    /// <summary>
    /// 获取当前处于有效授权状态的租约字典快照。
    /// </summary>
    public IReadOnlyDictionary<string, LeaseRequestBody> ActiveLeases
    {
        get
        {
            lock (_stateLock)
            {
                return new Dictionary<string, LeaseRequestBody>(_activeLeases, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// 获取当前已登记的所有受管操作字典快照。
    /// </summary>
    public IReadOnlyDictionary<string, HostOperation> Operations => _operations;

    /// <summary>
    /// 调度并在独立后台任务中执行租约决策。
    /// 将关闭检查与登记纳入同一同步裁决，请求等待与回调寿命分离管理。
    /// </summary>
    /// <param name="requestId">请求标识编号。</param>
    /// <param name="request">租约申请数据体。</param>
    /// <param name="callback">外部注入的租约决策回调委托。</param>
    /// <param name="connection">IPC 双工通信连接。</param>
    /// <param name="timeout">最大决策等待超时时限。</param>
    /// <param name="timeProvider">时间提供者。</param>
    /// <param name="sessionToken">会话级取消令牌。</param>
    /// <param name="responseWrapper">可选的外层信封包装器（用于子调用消息回复转发）。</param>
    /// <param name="invocationKey">发起申请的调用键（用于关联调用记录）。</param>
    /// <param name="registry">权威调用登记表。</param>
    /// <param name="originalRequestId">客户端原始内层请求编号（若与 requestId 隔离时透传）。</param>
    public void ScheduleLeaseDecision(
        long requestId,
        LeaseRequestBody request,
        Func<long, LeaseRequestBody, CancellationToken, ValueTask<LeaseDecision?>>? callback,
        IpcConnection connection,
        TimeSpan? timeout,
        TimeProvider timeProvider,
        CancellationToken sessionToken,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper = null,
        string? invocationKey = null,
        InvocationRegistry? registry = null,
        long? originalRequestId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(connection);

        var replyReqId = originalRequestId ?? requestId;

        ScheduleDecision<LeaseDecision>(
            requestId,
            "Lease",
            connection,
            timeout,
            timeProvider,
            sessionToken,
            callback is not null ? ct => callback(replyReqId, request, ct) : null,
            decision =>
            {
                if (decision is null)
                {
                    return Task.FromResult<IpcEnvelope?>(null);
                }

                if (decision.Granted)
                {
                    var leaseId = !string.IsNullOrWhiteSpace(decision.LeaseId)
                        ? decision.LeaseId
                        : "lease-" + Guid.NewGuid().ToString("N");

                    lock (_stateLock)
                    {
                        if (_isClosed)
                        {
                            var fault = ProtocolMessages.CreateRequestFault(replyReqId, "SessionClosed", "宿主协调器已关闭，拒绝登记新租约。");
                            return Task.FromResult<IpcEnvelope?>(fault);
                        }

                        if (_issuedLeaseIds.Contains(leaseId))
                        {
                            var denied = ProtocolMessages.CreateLeaseDenied(replyReqId, $"租约编号 '{leaseId}' 已在当前任务中签发，不能复用。");
                            return Task.FromResult<IpcEnvelope?>(denied);
                        }

                        if (invocationKey is not null && registry is not null)
                        {
                            if (!registry.TryTrackLease(invocationKey, leaseId))
                            {
                                var denied = ProtocolMessages.CreateLeaseDenied(replyReqId, "所属调用已停止或不存在，无法登记租约。");
                                return Task.FromResult<IpcEnvelope?>(denied);
                            }
                        }

                        _issuedLeaseIds.Add(leaseId);
                        _activeLeases[leaseId] = request;
                    }

                    return Task.FromResult<IpcEnvelope?>(ProtocolMessages.CreateLeaseGranted(replyReqId, leaseId));
                }

                return Task.FromResult<IpcEnvelope?>(ProtocolMessages.CreateLeaseDenied(replyReqId, decision.Reason ?? "Denied"));
            },
            responseWrapper,
            originalRequestId);
    }

    /// <summary>
    /// 集中撤销并清理指定标识集合的活动租约（用于子树回收联动）。
    /// </summary>
    /// <param name="leaseIds">要撤销的租约标识集合。</param>
    public void RevokeLeases(IEnumerable<string> leaseIds)
    {
        ArgumentNullException.ThrowIfNull(leaseIds);

        lock (_stateLock)
        {
            foreach (var leaseId in leaseIds)
            {
                if (_activeLeases.Remove(leaseId))
                {
                    _releasedLeases[leaseId] = 0;
                }
            }
        }
    }

    /// <summary>
    /// 调度并在独立后台任务中执行数据源恢复决策。
    /// 未配置回调时明确回复 Denied；校验合法动作及 HotUpdateSupported 约束。
    /// </summary>
    /// <param name="requestId">调度请求标识编号。</param>
    /// <param name="request">数据源失效恢复申请体。</param>
    /// <param name="callback">外部注入的数据源恢复决策回调委托。</param>
    /// <param name="connection">IPC 双工通信连接。</param>
    /// <param name="timeout">最大决策等待超时时限。</param>
    /// <param name="timeProvider">时间提供者。</param>
    /// <param name="sessionToken">会话级取消令牌。</param>
    /// <param name="responseWrapper">可选的外层信封包装器（用于子调用消息回复转发）。</param>
    /// <param name="originalRequestId">客户端原始内层请求编号（若与 requestId 隔离时透传）。</param>
    public void ScheduleSourceRepairDecision(
        long requestId,
        SourceRepairBody request,
        Func<long, SourceRepairBody, CancellationToken, ValueTask<SourceRepairDecision?>>? callback,
        IpcConnection connection,
        TimeSpan? timeout,
        TimeProvider timeProvider,
        CancellationToken sessionToken,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper = null,
        long? originalRequestId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(connection);

        var replyReqId = originalRequestId ?? requestId;

        if (callback is null)
        {
            var defaultDenied = ProtocolMessages.CreateSourceRepairResult(replyReqId, "Denied");
            StartDeliveryWorker(() => DeliverReplyOrCloseAsync(connection, defaultDenied, responseWrapper, sessionToken));
            return;
        }

        ScheduleDecision<SourceRepairDecision>(
            requestId,
            "SourceRepair",
            connection,
            timeout,
            timeProvider,
            sessionToken,
            ct => callback(replyReqId, request, ct),
            decision =>
            {
                if (decision is null)
                {
                    return Task.FromResult<IpcEnvelope?>(null);
                }

                var outcome = decision.Outcome;
                if (string.IsNullOrWhiteSpace(outcome) || outcome is not ("Reread" or "RestartRequired" or "Denied" or "Cancelled"))
                {
                    return Task.FromResult<IpcEnvelope?>(ProtocolMessages.CreateRequestFault(replyReqId, "InvalidSourceRepairOutcome", $"非法的数据源恢复裁决动作: '{outcome}'。"));
                }

                if (!request.HotUpdateSupported && string.Equals(outcome, "Reread", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult<IpcEnvelope?>(ProtocolMessages.CreateRequestFault(replyReqId, "InvalidSourceRepairOutcome", "数据源声明不支持热更新，不能指定 Reread 动作。"));
                }

                var replyEnv = ProtocolMessages.CreateSourceRepairResult(replyReqId, outcome);
                return Task.FromResult<IpcEnvelope?>(replyEnv);
            },
            responseWrapper,
            originalRequestId);
    }

    /// <summary>
    /// 三类审批共用的内部请求生命周期调度组件。
    /// 统一管理容量控制、请求超时终结、回调寿命隔离与执行槽位归还。
    /// </summary>
    private void ScheduleDecision<TDecision>(
        long requestId,
        string requestType,
        IpcConnection connection,
        TimeSpan? timeout,
        TimeProvider timeProvider,
        CancellationToken sessionToken,
        Func<CancellationToken, ValueTask<TDecision?>>? decisionCallback,
        Func<TDecision, Task<IpcEnvelope?>> buildReplyEnvelope,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper = null,
        long? originalRequestId = null)
    {
        var replyReqId = originalRequestId ?? requestId;
        if (!TryAdmitPendingRequest(requestId, requestType, replyReqId, connection, sessionToken, responseWrapper, out var pending))
        {
            return;
        }

        try
        {
            if (timeout is { } waitTimeout && waitTimeout > TimeSpan.Zero)
            {
                StartRequestTimeoutWorker(pending, requestId, requestType, replyReqId, connection, waitTimeout, timeProvider, sessionToken, responseWrapper);
            }

            if (decisionCallback is null)
            {
                // 未配置外部回调时保持有效等待，直至超时定时器到期发送 RequestTimeout
                return;
            }

            if (pending.TryRegisterWorker())
            {
                StartDecisionCallbackWorker(pending, requestId, requestType, replyReqId, connection, sessionToken, responseWrapper, decisionCallback, buildReplyEnvelope);
            }
        }
        catch
        {
            _pendingRequests.TryRemove(requestId, out _);
            pending.Abort();
            throw;
        }
        finally
        {
            pending.MarkInitializationComplete();
            ExitCoordinatorWorker();
        }
    }

    /// <summary>
    /// 在锁内原子裁决接纳：关闭、容量满与重复编号直接回复故障并返回 false；接纳成功后登记未决记录与工作者。
    /// </summary>
    /// <param name="requestId">治理请求编号。</param>
    /// <param name="requestType">请求类型名称，用于答复与诊断。</param>
    /// <param name="replyReqId">答复使用的客户端请求编号。</param>
    /// <param name="connection">根 IPC 连接。</param>
    /// <param name="sessionToken">会话级取消令牌。</param>
    /// <param name="responseWrapper">可选的外层信封包装器。</param>
    /// <param name="pending">接纳成功时的未决请求记录。</param>
    /// <returns>已接纳时为 true。</returns>
    private bool TryAdmitPendingRequest(
        long requestId,
        string requestType,
        long replyReqId,
        IpcConnection connection,
        CancellationToken sessionToken,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper,
        out GovernancePendingRequest pending)
    {
        lock (_stateLock)
        {
            if (!_acceptingNewWork)
            {
                var closedFault = ProtocolMessages.CreateRequestFault(replyReqId, "SessionClosed", "宿主协调器已处于关闭状态。");
                StartDeliveryWorker(() => DeliverReplyOrCloseAsync(connection, closedFault, responseWrapper, sessionToken));
                pending = null!;
                return false;
            }

            if (_pendingRequests.Count >= MaxPendingRequests)
            {
                var busyFault = ProtocolMessages.CreateRequestFault(replyReqId, "ServerBusy", "宿主治理请求处理队列已满。");
                StartDeliveryWorker(() => DeliverReplyOrCloseAsync(connection, busyFault, responseWrapper, sessionToken));
                pending = null!;
                return false;
            }

            _activeCoordinatorWorkers++;

            var callbackCts = new CancellationTokenSource();
            var timeoutCts = new CancellationTokenSource();
            pending = new GovernancePendingRequest(this, requestId, requestType, connection, callbackCts, timeoutCts);
            if (_pendingRequests.TryAdd(requestId, pending))
            {
                return true;
            }

            _activeCoordinatorWorkers--;
            callbackCts.Dispose();
            timeoutCts.Dispose();
            var duplicateFault = ProtocolMessages.CreateRequestFault(replyReqId, "DuplicateRequestId", $"检测到重复的治理请求编号 {requestId}。");
            StartDeliveryWorker(() => DeliverReplyOrCloseAsync(connection, duplicateFault, responseWrapper, sessionToken));
            pending = null!;
            return false;
        }
    }

    /// <summary>
    /// 启动有限超时工作者：到期且赢得裁决时交付 RequestTimeout，工作者退出后归还计数。
    /// </summary>
    /// <param name="pending">未决请求记录。</param>
    /// <param name="requestId">治理请求编号。</param>
    /// <param name="requestType">请求类型名称，用于答复与诊断。</param>
    /// <param name="replyReqId">答复使用的客户端请求编号。</param>
    /// <param name="connection">根 IPC 连接。</param>
    /// <param name="waitTimeout">请求等待时限。</param>
    /// <param name="timeProvider">时间提供者。</param>
    /// <param name="sessionToken">会话级取消令牌。</param>
    /// <param name="responseWrapper">可选的外层信封包装器。</param>
    private void StartRequestTimeoutWorker(
        GovernancePendingRequest pending,
        long requestId,
        string requestType,
        long replyReqId,
        IpcConnection connection,
        TimeSpan waitTimeout,
        TimeProvider timeProvider,
        CancellationToken sessionToken,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper)
    {
        if (!pending.TryRegisterWorker())
        {
            return;
        }

        var timeoutTask = Task.Run(async () =>
        {
            try
            {
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, pending.RequestTimeoutCts.Token);
                    await Task.Delay(waitTimeout, timeProvider, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (pending.TryTriggerTimeout())
                {
                    _pendingRequests.TryRemove(requestId, out _);
                    await DeliverAdjudicatedReplySafelyAsync(
                        connection,
                        () => Task.FromResult<IpcEnvelope?>(ProtocolMessages.CreateRequestFault(replyReqId, "RequestTimeout", $"宿主治理请求 {requestType} 决策执行超时。")),
                        replyReqId,
                        requestType,
                        responseWrapper,
                        sessionToken).ConfigureAwait(false);
                }
            }
            finally
            {
                pending.OnWorkerExit();
            }
        });

        _ = timeoutTask.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
    }

    /// <summary>
    /// 启动决策回调工作者：先取得并发门名额，再调用外部回调并交付裁决答复。
    /// </summary>
    /// <typeparam name="TDecision">决策结果类型。</typeparam>
    /// <param name="pending">未决请求记录。</param>
    /// <param name="requestId">治理请求编号。</param>
    /// <param name="requestType">请求类型名称，用于答复与诊断。</param>
    /// <param name="replyReqId">答复使用的客户端请求编号。</param>
    /// <param name="connection">根 IPC 连接。</param>
    /// <param name="sessionToken">会话级取消令牌。</param>
    /// <param name="responseWrapper">可选的外层信封包装器。</param>
    /// <param name="decisionCallback">外部决策回调。</param>
    /// <param name="buildReplyEnvelope">依据裁决结果构造答复信封的委托。</param>
    private void StartDecisionCallbackWorker<TDecision>(
        GovernancePendingRequest pending,
        long requestId,
        string requestType,
        long replyReqId,
        IpcConnection connection,
        CancellationToken sessionToken,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper,
        Func<CancellationToken, ValueTask<TDecision?>> decisionCallback,
        Func<TDecision, Task<IpcEnvelope?>> buildReplyEnvelope)
    {
        var callbackTask = Task.Run(async () =>
        {
            var gateAcquired = false;
            try
            {
                if (!await TryEnterDecisionGateAsync(pending, sessionToken).ConfigureAwait(false))
                {
                    return;
                }

                gateAcquired = true;

                if (pending.IsCompleted)
                {
                    return;
                }

                var decision = await InvokeDecisionCallbackAsync(pending, requestId, requestType, replyReqId, connection, sessionToken, responseWrapper, decisionCallback).ConfigureAwait(false);

                // 回调未提供裁决结果或已在本步骤交付答复时，继续保留有效等待直至超时
                if (decision is null || !pending.TryMarkCompleted())
                {
                    return;
                }

                _pendingRequests.TryRemove(requestId, out _);

                await DeliverAdjudicatedReplySafelyAsync(
                    connection,
                    () => buildReplyEnvelope(decision),
                    replyReqId,
                    requestType,
                    responseWrapper,
                    sessionToken).ConfigureAwait(false);
            }
            finally
            {
                if (gateAcquired)
                {
                    _decisionExecutionGate.Release();
                }

                pending.OnWorkerExit();
            }
        });

        _ = callbackTask.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
    }

    /// <summary>
    /// 在有限等待内取得决策执行槽位；超时或关闭提前结束等待时放弃槽位。
    /// </summary>
    /// <param name="pending">未决请求记录。</param>
    /// <param name="sessionToken">会话级取消令牌。</param>
    /// <returns>已取得槽位时为 true。</returns>
    private async Task<bool> TryEnterDecisionGateAsync(GovernancePendingRequest pending, CancellationToken sessionToken)
    {
        using var slotWaitCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, pending.RequestTimeoutCts.Token);
        try
        {
            await _decisionExecutionGate.WaitAsync(slotWaitCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            // 超时或关闭提前结束等待，放弃执行槽位
            return false;
        }
    }

    /// <summary>
    /// 调用外部决策回调并处置其取消与异常：超时取消交付 RequestTimeout，异常按是否赢得裁决交付 DecisionFault 或只记录迟到诊断。
    /// </summary>
    /// <typeparam name="TDecision">决策结果类型。</typeparam>
    /// <param name="pending">未决请求记录。</param>
    /// <param name="requestId">治理请求编号。</param>
    /// <param name="requestType">请求类型名称，用于答复与诊断。</param>
    /// <param name="replyReqId">答复使用的客户端请求编号。</param>
    /// <param name="connection">根 IPC 连接。</param>
    /// <param name="sessionToken">会话级取消令牌。</param>
    /// <param name="responseWrapper">可选的外层信封包装器。</param>
    /// <param name="decisionCallback">外部决策回调。</param>
    /// <returns>可用的裁决结果；已交付答复或保持等待时为 null。</returns>
    private async Task<TDecision?> InvokeDecisionCallbackAsync<TDecision>(
        GovernancePendingRequest pending,
        long requestId,
        string requestType,
        long replyReqId,
        IpcConnection connection,
        CancellationToken sessionToken,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper,
        Func<CancellationToken, ValueTask<TDecision?>> decisionCallback)
    {
        try
        {
            return await decisionCallback(pending.CallbackCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (pending.CallbackCts.IsCancellationRequested && !sessionToken.IsCancellationRequested)
        {
            if (pending.TryTriggerTimeout())
            {
                _pendingRequests.TryRemove(requestId, out _);
                await DeliverAdjudicatedReplySafelyAsync(
                    connection,
                    () => Task.FromResult<IpcEnvelope?>(ProtocolMessages.CreateRequestFault(replyReqId, "RequestTimeout", "宿主治理决策执行超时。")),
                    replyReqId,
                    requestType,
                    responseWrapper,
                    sessionToken).ConfigureAwait(false);
            }

            return default;
        }
        catch (Exception callbackEx)
        {
            RecordIssue("DecisionCallback", callbackEx);
            if (pending.TryMarkCompleted())
            {
                _pendingRequests.TryRemove(requestId, out _);
                await DeliverAdjudicatedReplySafelyAsync(
                    connection,
                    () => Task.FromResult<IpcEnvelope?>(ProtocolMessages.CreateRequestFault(replyReqId, "DecisionFault", $"宿主治理决策发生异常: {DescribeSafeError(callbackEx)}")),
                    replyReqId,
                    requestType,
                    responseWrapper,
                    sessionToken).ConfigureAwait(false);
            }
            else
            {
                // 超时或关闭已完成裁决，迟到回调异常仅记录脱敏诊断并安全退出，严禁关闭会话连接
                ReportDiagnostic($"[HostGovernanceCoordinator] 治理请求 {requestType} 裁决已由其他任务完成，忽略迟到回调异常: {DescribeSafeError(callbackEx)}");
            }

            return default;
        }
    }

    /// <summary>
    /// 处理来自技能端的租约释放通知（LeaseRelease）。
    /// </summary>
    /// <param name="release">租约释放通知数据体。</param>
    public void ReleaseLease(LeaseReleaseBody release)
    {
        ArgumentNullException.ThrowIfNull(release);

        lock (_stateLock)
        {
            _activeLeases.Remove(release.LeaseId);
        }

        if (_releasedLeases.Count >= MaxReleasedLeasesCount)
        {
            _releasedLeases.Clear();
        }

        _releasedLeases.TryAdd(release.LeaseId, 0);
    }

    /// <summary>
    /// 回收所有处于活跃授权状态的租约记录并原子置位关闭状态。
    /// 将关闭与清空纳入同一同步互斥裁决，杜绝并发新增。
    /// </summary>
    public void RecycleAllLeases()
    {
        lock (_stateLock)
        {
            _isClosed = true;
            _activeLeases.Clear();
        }
    }

    /// <summary>
    /// 登记受管操作并仲裁初始生效策略。
    /// </summary>
    /// <param name="registration">操作登记请求体。</param>
    /// <param name="policyOverride">宿主全局策略覆盖（可选）。</param>
    /// <param name="timeProvider">时间提供者。</param>
    /// <returns>初始化的受管操作视图实例。</returns>
    /// <exception cref="ProtocolException">操作键重复或引用的父操作不存在时抛出。</exception>
    public HostOperation RegisterOperation(
        OperationRegisterBody registration,
        RunPolicyBody? policyOverride,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (_operations.ContainsKey(registration.OperationKey))
        {
            throw new ProtocolException($"受管操作键 '{registration.OperationKey}' 重复登记。");
        }

        if (!string.IsNullOrWhiteSpace(registration.ParentOperationKey)
            && !_operations.ContainsKey(registration.ParentOperationKey))
        {
            throw new ProtocolException($"受管操作引用的父操作 '{registration.ParentOperationKey}' 尚未登记。");
        }

        if (policyOverride is not null && !RunPolicyBody.Validate(policyOverride, out var overrideErr))
        {
            throw new ProtocolException($"宿主策略覆盖非法：{overrideErr}");
        }

        if (registration.SuggestedPolicy is not null && !RunPolicyBody.Validate(registration.SuggestedPolicy, out var regErr))
        {
            throw new ProtocolException($"技能建议策略非法：{regErr}");
        }

        var effectivePolicy = policyOverride ?? registration.SuggestedPolicy ?? RunPolicyBody.Default;
        var policySource = policyOverride is not null ? "HostOverride" : "SkillSuggested";
        const long initialVersion = 1;

        var operation = new HostOperation(
            registration,
            effectivePolicy,
            policySource,
            initialVersion,
            timeProvider);

        _operations[registration.OperationKey] = operation;
        return operation;
    }

    /// <summary>
    /// 为向技能发出的策略动态更新登记未决对账记录。
    /// 验证策略合法性；若操作不存在、已终态或容量超限则拒绝登记。
    /// 启动独立的确认超时观察任务，超时未确认从未决表清理并标记操作。
    /// </summary>
    /// <param name="requestId">更新请求分配的唯一编号。</param>
    /// <param name="update">待发送的目标策略更新模型。</param>
    /// <param name="timeout">确认等待超时时限。</param>
    /// <param name="maxCapacity">未决更新最大容量限制。</param>
    /// <param name="timeProvider">时间提供者。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>登记成功返回 true；若操作已结束、策略非法或容量已满返回 false。</returns>
    public bool TryRegisterPolicyUpdate(
        long requestId,
        RunPolicyUpdate update,
        TimeSpan timeout,
        int maxCapacity,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (!CanRegisterPolicyUpdate(update, maxCapacity))
        {
            return false;
        }

        if (!TryAddPendingPolicyUpdate(requestId, update, timeout, out var timeoutCts))
        {
            return false;
        }

        if (timeout > TimeSpan.Zero && timeoutCts is not null)
        {
            return TryStartPolicyUpdateTimeoutWorker(requestId, update, timeout, timeProvider, cancellationToken, timeoutCts);
        }

        return true;
    }

    /// <summary>
    /// 登记前的资格核验：策略合法、未决容量未满、目标操作存在且未结束。
    /// </summary>
    /// <param name="update">待发送的目标策略更新模型。</param>
    /// <param name="maxCapacity">未决更新最大容量限制。</param>
    /// <returns>可以登记时为 true。</returns>
    private bool CanRegisterPolicyUpdate(RunPolicyUpdate update, int maxCapacity)
    {
        if (!RunPolicyBody.Validate(update.Policy, out _))
        {
            return false;
        }

        if (_pendingPolicyUpdates.Count >= maxCapacity)
        {
            return false;
        }

        return _operations.TryGetValue(update.OperationKey, out var operation) && !operation.IsEnded;
    }

    /// <summary>
    /// 在锁内原子登记未决策略更新：关闭或容量满时拒绝；需要超时观察时登记工作者并建立独立取消源。
    /// </summary>
    /// <param name="requestId">更新请求分配的唯一编号。</param>
    /// <param name="update">待发送的目标策略更新模型。</param>
    /// <param name="timeout">确认等待超时时限。</param>
    /// <param name="timeoutCts">登记成功且需要超时观察时的确认取消源。</param>
    /// <returns>登记成功时为 true。</returns>
    private bool TryAddPendingPolicyUpdate(
        long requestId,
        RunPolicyUpdate update,
        TimeSpan timeout,
        out CancellationTokenSource? timeoutCts)
    {
        timeoutCts = null;
        lock (_stateLock)
        {
            if (!_acceptingNewWork || _pendingPolicyUpdates.Count >= MaxPendingRequests)
            {
                return false;
            }

            if (timeout > TimeSpan.Zero)
            {
                if (_allWorkersDrainedTcs.Task.IsCompleted)
                {
                    return false;
                }

                _activeCoordinatorWorkers++;
                timeoutCts = new CancellationTokenSource();
            }

            var pending = new PendingPolicyUpdate(
                requestId,
                update.OperationKey,
                update.Version,
                update.Source,
                update.Policy,
                timeoutCts ?? new CancellationTokenSource());

            if (_pendingPolicyUpdates.TryAdd(requestId, pending))
            {
                return true;
            }

            if (timeout > TimeSpan.Zero)
            {
                _activeCoordinatorWorkers--;
            }

            timeoutCts?.Dispose();
            timeoutCts = null;
            return false;
        }
    }

    /// <summary>
    /// 启动确认超时观察任务：到期未确认时竞争单次原子裁决并标记操作超时；启动失败时回滚登记与工作者计数。
    /// </summary>
    /// <param name="requestId">更新请求分配的唯一编号。</param>
    /// <param name="update">待发送的目标策略更新模型。</param>
    /// <param name="timeout">确认等待超时时限。</param>
    /// <param name="timeProvider">时间提供者。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="timeoutCts">确认取消源。</param>
    /// <returns>超时观察任务已启动时为 true。</returns>
    private bool TryStartPolicyUpdateTimeoutWorker(
        long requestId,
        RunPolicyUpdate update,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        CancellationTokenSource timeoutCts)
    {
        try
        {
            var timeoutTask = Task.Run(async () =>
            {
                try
                {
                    try
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                        await Task.Delay(timeout, timeProvider, linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    finally
                    {
                        try { timeoutCts.Dispose(); } catch { }
                    }

                    if (_pendingPolicyUpdates.TryGetValue(requestId, out var timedOutPending)
                        && timedOutPending.Version == update.Version)
                    {
                        // 竞争单次原子完成裁决，防止与确认或拒绝交错覆盖
                        if (!timedOutPending.TryComplete())
                        {
                            return;
                        }

                        _pendingPolicyUpdates.TryRemove(requestId, out _);
                        if (_operations.TryGetValue(update.OperationKey, out var timedOutOp))
                        {
                            timedOutOp.RecordPolicyUpdateOutcome("UpdateTimeout", update.Version);
                        }
                    }
                }
                finally
                {
                    ExitCoordinatorWorker();
                }
            });

            _ = timeoutTask.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
            return true;
        }
        catch
        {
            _pendingPolicyUpdates.TryRemove(requestId, out _);
            timeoutCts.Dispose();
            ExitCoordinatorWorker();
            return false;
        }
    }

    /// <summary>
    /// 处理来自技能端的策略应用确认（PolicyApplied）。
    /// 先核验内容完全匹配再移除未决记录，不匹配不误删记录。
    /// 统一 PolicyApplied.Version 语义为原请求的目标版本；Applied 才更新生效策略。
    /// 确认与超时竞争同一单次原子裁决，杜绝确认后被超时覆盖。
    /// </summary>
    /// <param name="requestId">可选请求编号。</param>
    /// <param name="applied">应用确认数据体。</param>
    /// <param name="matchedOperation">成功匹配的操作实例。</param>
    /// <returns>若匹配成功返回 true；若未找到对应受管操作返回 false。</returns>
    public bool HandlePolicyApplied(long? requestId, PolicyAppliedBody applied, out HostOperation? matchedOperation)
    {
        matchedOperation = null;
        if (!_operations.TryGetValue(applied.OperationKey, out matchedOperation) || matchedOperation is null)
        {
            return false;
        }

        if (requestId is not long reqId || !_pendingPolicyUpdates.TryGetValue(reqId, out var pendingUpdate))
        {
            return false;
        }

        // 先核验内容匹配，不匹配则绝不提前移除真实记录
        if (!string.Equals(pendingUpdate.OperationKey, applied.OperationKey, StringComparison.Ordinal)
            || pendingUpdate.Version != applied.Version
            || !string.Equals(pendingUpdate.Source, applied.Source, StringComparison.Ordinal))
        {
            return false;
        }

        // 竞争单次原子完成裁决，若已被超时抢先裁决则直接退出
        if (!pendingUpdate.TryComplete())
        {
            return false;
        }

        _pendingPolicyUpdates.TryRemove(reqId, out _);
        pendingUpdate.CancelTimeout();

        if (string.Equals(applied.Outcome, "Applied", StringComparison.OrdinalIgnoreCase))
        {
            if (applied.Version > matchedOperation.PolicyVersion)
            {
                matchedOperation.ApplyPolicy(pendingUpdate.TargetPolicy, applied.Version, applied.Source, applied.Outcome);
            }
        }
        else
        {
            matchedOperation.RecordPolicyUpdateOutcome(applied.Outcome, applied.Version);
        }

        return true;
    }

    /// <summary>
    /// 处理策略更新请求被技能拒绝或发生故障。
    /// </summary>
    /// <param name="requestId">请求编号。</param>
    public void HandlePolicyFault(long requestId)
    {
        if (_pendingPolicyUpdates.TryGetValue(requestId, out var pendingUpdate))
        {
            if (!pendingUpdate.TryComplete())
            {
                return;
            }

            _pendingPolicyUpdates.TryRemove(requestId, out _);
            pendingUpdate.CancelTimeout();
            if (_operations.TryGetValue(pendingUpdate.OperationKey, out var op))
            {
                op.RecordPolicyUpdateOutcome("Rejected", pendingUpdate.Version);
            }
        }
    }
}
