using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// 宿主端执行协调器的治理与受管操作消息分部。
/// 租约、补参、来源恢复与操作登记统一分配全局调度编号后交给治理协调器裁决；
/// 操作状态通知与策略答复先核验操作归属，再按单次裁决结果更新快照。
/// </summary>
internal sealed partial class HostExecutionCoordinator
{
    /// <summary>
    /// 处理治理与受管操作消息：租约申请与释放、补参申请、来源恢复申请、操作登记、
    /// 操作到期、操作状态通知、策略应用确认与策略更新失败答复。
    /// </summary>
    /// <param name="envelope">内层信封。</param>
    /// <param name="originKey">内层消息的直接来源调用键。</param>
    /// <param name="responseWrapper">可选的信封包装委托。</param>
    /// <param name="childRoute">外层中继路由链。</param>
    /// <returns>本组处置结果。</returns>
    private async Task<MessageDisposition> HandleGovernanceMessageAsync(
        IpcEnvelope envelope,
        string originKey,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper,
        ChildMessageRoute? childRoute)
    {
        if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.LeaseRequest, StringComparison.Ordinal))
        {
            HandleLeaseRequest(envelope, originKey, responseWrapper);
            return MessageDisposition.Handled;
        }

        if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.LeaseRelease, StringComparison.Ordinal))
        {
            await HandleLeaseReleaseAsync(envelope, originKey, childRoute).ConfigureAwait(false);
            return MessageDisposition.Handled;
        }

        if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.ParamRequest, StringComparison.Ordinal))
        {
            HandleParamRequest(envelope, responseWrapper);
            return MessageDisposition.Handled;
        }

        if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.SourceRepair, StringComparison.Ordinal))
        {
            HandleSourceRepairRequest(envelope, responseWrapper);
            return MessageDisposition.Handled;
        }

        if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.OperationRegister, StringComparison.Ordinal))
        {
            await HandleOperationRegisterAsync(envelope, originKey, responseWrapper).ConfigureAwait(false);
            return MessageDisposition.Handled;
        }

        if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.OperationExpired, StringComparison.Ordinal))
        {
            await HandleOperationExpiredAsync(envelope, originKey, childRoute).ConfigureAwait(false);
            return MessageDisposition.Handled;
        }

        if (IsOperationStateNotification(envelope.Type))
        {
            await HandleOperationStateAsync(envelope, originKey, childRoute).ConfigureAwait(false);
            return MessageDisposition.Handled;
        }

        if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.PolicyApplied, StringComparison.Ordinal))
        {
            await HandlePolicyAppliedAsync(envelope, originKey, childRoute).ConfigureAwait(false);
            return MessageDisposition.Handled;
        }

        if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.RequestFault, StringComparison.Ordinal))
        {
            await HandlePolicyUpdateFaultAsync(envelope, originKey, childRoute).ConfigureAwait(false);
            return MessageDisposition.Handled;
        }

        return MessageDisposition.NotHandled;
    }

    /// <summary>
    /// 分配全局调度编号并交给治理协调器裁决租约申请；原请求编号随调度信息透传，供答复包装使用。
    /// </summary>
    /// <param name="envelope">内层信封。</param>
    /// <param name="originKey">内层消息的直接来源调用键。</param>
    /// <param name="responseWrapper">可选的信封包装委托。</param>
    private void HandleLeaseRequest(IpcEnvelope envelope, string originKey, Func<IpcEnvelope, IpcEnvelope>? responseWrapper)
    {
        if (envelope.Id is not long requestId)
        {
            throw new ProtocolException("LeaseRequest 缺少必需的请求编号。");
        }

        var leaseBody = ProtocolMessages.ReadBody<LeaseRequestBody>(envelope);
        var dispatchId = Interlocked.Increment(ref _nextGovernanceRequestId);
        governance.ScheduleLeaseDecision(
            dispatchId,
            leaseBody,
            options.LeaseDecisionCallback,
            connection,
            options.RequestTimeout,
            options.TimeProvider,
            sessionToken,
            responseWrapper,
            originKey,
            registry,
            originalRequestId: requestId);
    }

    /// <summary>
    /// 核验租约归属后释放单条租约；非持有者释放只记诊断，随后向外层确认通知。
    /// </summary>
    /// <param name="envelope">内层信封。</param>
    /// <param name="originKey">内层消息的直接来源调用键。</param>
    /// <param name="childRoute">外层中继路由链。</param>
    /// <returns>确认写出的异步完成任务。</returns>
    private async Task HandleLeaseReleaseAsync(IpcEnvelope envelope, string originKey, ChildMessageRoute? childRoute)
    {
        var release = ProtocolMessages.ReadBody<LeaseReleaseBody>(envelope);
        if (registry.TryReleaseLease(originKey, release.LeaseId))
        {
            governance.ReleaseLease(release);
        }
        else
        {
            callbackDispatcher.DispatchDiagnostic($"调用 '{originKey}' 尝试释放未持有的租约 '{release.LeaseId}'。");
        }

        await AckChildNotificationAsync(childRoute, _activeSessionToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 分配全局调度编号并交给治理协调器裁决补参申请；补参校验器由治理按操作策略选择。
    /// </summary>
    /// <param name="envelope">内层信封。</param>
    /// <param name="responseWrapper">可选的信封包装委托。</param>
    private void HandleParamRequest(IpcEnvelope envelope, Func<IpcEnvelope, IpcEnvelope>? responseWrapper)
    {
        if (envelope.Id is not long requestId)
        {
            throw new ProtocolException("ParamRequest 缺少必需的请求编号。");
        }

        var paramBody = ProtocolMessages.ReadBody<ParamRequestBody>(envelope);
        var dispatchId = Interlocked.Increment(ref _nextGovernanceRequestId);
        governance.ScheduleParamDecision(
            dispatchId,
            paramBody,
            options.ParamDecisionCallback,
            connection,
            options.RequestTimeout,
            options.TimeProvider,
            sessionToken,
            validator: null,
            responseWrapper: responseWrapper,
            originalRequestId: requestId);
    }

    /// <summary>
    /// 分配全局调度编号并交给治理协调器裁决数据源失效恢复申请。
    /// </summary>
    /// <param name="envelope">内层信封。</param>
    /// <param name="responseWrapper">可选的信封包装委托。</param>
    private void HandleSourceRepairRequest(IpcEnvelope envelope, Func<IpcEnvelope, IpcEnvelope>? responseWrapper)
    {
        if (envelope.Id is not long requestId)
        {
            throw new ProtocolException("SourceRepair 缺少必需的请求编号。");
        }

        var repairBody = ProtocolMessages.ReadBody<SourceRepairBody>(envelope);
        var dispatchId = Interlocked.Increment(ref _nextGovernanceRequestId);
        governance.ScheduleSourceRepairDecision(
            dispatchId,
            repairBody,
            options.SourceRepairCallback,
            connection,
            options.RequestTimeout,
            options.TimeProvider,
            sessionToken,
            responseWrapper: responseWrapper,
            originalRequestId: requestId);
    }

    /// <summary>
    /// 登记受管操作：归属登记与操作创建协同原子化，操作创建失败时仅回滚本次归属登记。
    /// 登记成功后回发 <c>OperationAccepted</c> 并派发初始操作快照。
    /// </summary>
    /// <param name="envelope">内层信封。</param>
    /// <param name="originKey">内层消息的直接来源调用键。</param>
    /// <param name="responseWrapper">可选的信封包装委托。</param>
    /// <returns>答复写出的异步完成任务。</returns>
    private async Task HandleOperationRegisterAsync(
        IpcEnvelope envelope,
        string originKey,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper)
    {
        if (envelope.Id is not long requestId)
        {
            throw new ProtocolException("OperationRegister 缺少必需的请求编号。");
        }

        var registration = ProtocolMessages.ReadBody<OperationRegisterBody>(envelope);
        if (!registry.TryTrackOperation(originKey, registration.OperationKey))
        {
            throw new InvalidOperationException($"调用 '{originKey}' 已不存在或已停止，无法为其登记受管操作 '{registration.OperationKey}'。");
        }

        HostOperation operation;
        try
        {
            operation = governance.RegisterOperation(registration, options.PolicyOverride, options.TimeProvider);
        }
        catch
        {
            registry.UntrackOperation(originKey, registration.OperationKey);
            throw;
        }

        var acceptedEnvelope = ProtocolMessages.CreateOperationAccepted(
            requestId,
            operation.Snapshot().OperationKey,
            operation.PolicyVersion,
            operation.PolicySource,
            operation.Policy.ToWireV3());

        await SendReplyAsync(acceptedEnvelope, responseWrapper, connection, _activeSessionToken).ConfigureAwait(false);
        callbackDispatcher.DispatchSnapshot(operation.Snapshot());
    }

    /// <summary>
    /// 处理受管操作到期通知：按操作归属核验来源，仅采纳与本地策略版本一致或与未决更新一致的到期声明，
    /// 旧版本、来源或动作不符以及未知版本只记诊断，随后确认外层通知。
    /// </summary>
    /// <param name="envelope">内层信封。</param>
    /// <param name="originKey">内层消息的直接来源调用键。</param>
    /// <param name="childRoute">外层中继路由链。</param>
    /// <returns>确认写出的异步完成任务。</returns>
    private async Task HandleOperationExpiredAsync(IpcEnvelope envelope, string originKey, ChildMessageRoute? childRoute)
    {
        var expired = ProtocolMessages.ReadBody<OperationExpiredBody>(envelope);
        var opOwner = registry.GetOperationOwner(expired.OperationKey);
        if (!IsOriginAuthorizedForOperation(originKey, opOwner))
        {
            callbackDispatcher.DispatchDiagnostic($"调用 '{originKey}' 无权处理操作 '{expired.OperationKey}' (归属 '{opOwner}') 的到期通知。");
            await AckChildNotificationAsync(childRoute, _activeSessionToken).ConfigureAwait(false);
            return;
        }

        if (!governance.TryGetOperation(expired.OperationKey, out var operation) || operation is null)
        {
            callbackDispatcher.DispatchDiagnostic($"收到未知操作的 OperationExpired: {expired.OperationKey}。");
        }
        else if (expired.PolicyVersion < operation.PolicyVersion)
        {
            callbackDispatcher.DispatchDiagnostic($"忽略旧版本 OperationExpired: 操作 {expired.OperationKey} 版本 {expired.PolicyVersion} < {operation.PolicyVersion}。");
        }
        else if (ResolveExpiredPolicy(expired, operation) is { } authoritativePolicy)
        {
            if (IsExpiredPolicyMatching(expired, authoritativePolicy.Source, authoritativePolicy.Action))
            {
                if (IsStopOrCancel(expired.DeadlineAction))
                {
                    operation.StopReason = "Deadline";
                }

                callbackDispatcher.DispatchSnapshot(operation.Snapshot());
            }
            else
            {
                callbackDispatcher.DispatchDiagnostic($"收到不匹配的 OperationExpired (来源或动作不符): 操作 {expired.OperationKey} 版本 {expired.PolicyVersion}。");
            }
        }
        else
        {
            callbackDispatcher.DispatchDiagnostic($"收到未知版本 OperationExpired: 操作 {expired.OperationKey} 版本 {expired.PolicyVersion}。");
        }

        await AckChildNotificationAsync(childRoute, _activeSessionToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 解析到期声明对应的权威策略来源与期限动作：同版本取操作自身策略，更高版本取已登记的未决更新；
    /// 来源或动作缺失时返回 null，表示该版本没有可核对的权威策略。
    /// </summary>
    /// <param name="expired">到期通知。</param>
    /// <param name="operation">本地受管操作。</param>
    /// <returns>权威策略来源与期限动作；无法确定时为 null。</returns>
    private (string Source, string Action)? ResolveExpiredPolicy(OperationExpiredBody expired, HostOperation operation)
    {
        if (expired.PolicyVersion == operation.PolicyVersion)
        {
            return operation.PolicySource is { } source && operation.Policy.DeadlineAction is { } action
                ? (source, action)
                : null;
        }

        if (governance.TryGetPendingPolicyUpdate(expired.OperationKey, expired.PolicyVersion, out var pendingInfo) && pendingInfo is not null)
        {
            return (pendingInfo.Value.Source, pendingInfo.Value.DeadlineAction);
        }

        return null;
    }

    /// <summary>
    /// 判断到期声明与本地权威策略是否一致：来源按序数严格比较，动作只比较是否属于停止类语义。
    /// </summary>
    /// <param name="expired">到期通知。</param>
    /// <param name="source">本地权威策略来源。</param>
    /// <param name="action">本地权威期限动作。</param>
    /// <returns>来源与动作均匹配时为 true。</returns>
    private static bool IsExpiredPolicyMatching(OperationExpiredBody expired, string source, string action)
    {
        var sourceMatch = string.Equals(expired.Source, source, StringComparison.Ordinal);
        var actionMatch = IsStopOrCancel(expired.DeadlineAction) == IsStopOrCancel(action);
        return sourceMatch && actionMatch;
    }

    /// <summary>
    /// 判断期限动作是否表示停止或取消。
    /// </summary>
    /// <param name="action">期限动作文本。</param>
    /// <returns>属于停止类语义时为 true。</returns>
    private static bool IsStopOrCancel(string? action) =>
        string.Equals(action, "Stop", StringComparison.OrdinalIgnoreCase)
        || string.Equals(action, "Cancel", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 判断消息类型是否属于受管操作状态通知；无响应通知由 SDK 以独立类型上报，与状态变更共用状态体。
    /// </summary>
    /// <param name="messageType">消息类型标识。</param>
    /// <returns>属于操作状态通知时为 true。</returns>
    private static bool IsOperationStateNotification(string messageType) =>
        string.Equals(messageType, ProtocolConstants.MessageTypes.OperationStarted, StringComparison.Ordinal)
        || string.Equals(messageType, ProtocolConstants.MessageTypes.AttemptEnded, StringComparison.Ordinal)
        || string.Equals(messageType, ProtocolConstants.MessageTypes.OperationEnd, StringComparison.Ordinal)
        || string.Equals(messageType, ProtocolConstants.MessageTypes.OperationState, StringComparison.Ordinal)
        || string.Equals(messageType, OperationUnresponsiveType, StringComparison.Ordinal);

    /// <summary>
    /// 处理受管操作状态变更与无响应通知：核验归属后在操作状态机内采纳，派发新快照，
    /// 无响应时按操作归属联动根任务取消或子树停止，最后确认外层通知。
    /// </summary>
    /// <param name="envelope">内层信封。</param>
    /// <param name="originKey">内层消息的直接来源调用键。</param>
    /// <param name="childRoute">外层中继路由链。</param>
    /// <returns>确认写出的异步完成任务。</returns>
    private async Task HandleOperationStateAsync(IpcEnvelope envelope, string originKey, ChildMessageRoute? childRoute)
    {
        var stateBody = ProtocolMessages.ReadBody<OperationStateBody>(envelope);
        var opOwner = registry.GetOperationOwner(stateBody.OperationKey);
        if (!IsOriginAuthorizedForOperation(originKey, opOwner))
        {
            callbackDispatcher.DispatchDiagnostic($"调用 '{originKey}' 无权变更操作 '{stateBody.OperationKey}' 状态 (归属 '{opOwner}')。");
            await AckChildNotificationAsync(childRoute, _activeSessionToken).ConfigureAwait(false);
            return;
        }

        if (governance.TryGetOperation(stateBody.OperationKey, out var operation) && operation is not null)
        {
            operation.ApplyState(envelope.Type, stateBody);
            callbackDispatcher.DispatchSnapshot(operation.Snapshot());

            if (string.Equals(stateBody.State, "Unresponsive", StringComparison.Ordinal)
                || string.Equals(envelope.Type, OperationUnresponsiveType, StringComparison.Ordinal))
            {
                var reason = !string.IsNullOrWhiteSpace(stateBody.Reason) ? stateBody.Reason : "受管操作未响应。";
                HandleUnresponsiveOperation(opOwner, stateBody.OperationKey, reason);
            }
        }

        await AckChildNotificationAsync(childRoute, _activeSessionToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 隔离无响应操作的影响：根调用所属操作取消整任务执行，子调用所属操作联动停止其子树。
    /// 归属未知时只保留已派发的操作快照，不猜测回收对象。
    /// </summary>
    /// <param name="opOwner">操作所属调用键。</param>
    /// <param name="operationKey">受管操作键。</param>
    /// <param name="reason">无响应原因说明。</param>
    private void HandleUnresponsiveOperation(string? opOwner, string operationKey, string reason)
    {
        if (string.Equals(opOwner, rootRecord.InvocationKey, StringComparison.Ordinal))
        {
            _faultKind = OperationUnresponsiveType;
            _faultMessage = reason;
            executionCts.Cancel();
            return;
        }

        if (opOwner is not null && registry.TryGet(opOwner, out var subRecord) && subRecord is not null)
        {
            callbackDispatcher.DispatchDiagnostic($"子调用 '{opOwner}' 的受管操作 '{operationKey}' 未响应，联动停止子树。");
            var subtree = registry.RequestStopSubtree(opOwner);
            ScheduleUnresponsiveStop(opOwner, subtree);
        }
    }

    /// <summary>
    /// 处理策略应用确认：核验操作归属后由治理协调器完成单次裁决，采纳时派发新快照，最后确认通知。
    /// </summary>
    /// <param name="envelope">内层信封。</param>
    /// <param name="originKey">内层消息的直接来源调用键。</param>
    /// <param name="childRoute">外层中继路由链。</param>
    /// <returns>确认写出的异步完成任务。</returns>
    private async Task HandlePolicyAppliedAsync(IpcEnvelope envelope, string originKey, ChildMessageRoute? childRoute)
    {
        var applied = ProtocolMessages.ReadBody<PolicyAppliedBody>(envelope);
        var opOwner = registry.GetOperationOwner(applied.OperationKey);
        if (!IsOriginAuthorizedForOperation(originKey, opOwner))
        {
            callbackDispatcher.DispatchDiagnostic($"调用 '{originKey}' 无权确认操作 '{applied.OperationKey}' 的策略变更 (归属 '{opOwner}')。");
            await AckChildNotificationAsync(childRoute, _activeSessionToken).ConfigureAwait(false);
            return;
        }

        if (governance.HandlePolicyApplied(envelope.Id, applied, out var operation) && operation is not null)
        {
            callbackDispatcher.DispatchSnapshot(operation.Snapshot());
        }

        await AckChildNotificationAsync(childRoute, _activeSessionToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 处理针对策略更新的拒绝或失败答复：先核验其目标操作归属，再按请求编号结束未决更新，最后确认通知。
    /// </summary>
    /// <param name="envelope">内层信封。</param>
    /// <param name="originKey">内层消息的直接来源调用键。</param>
    /// <param name="childRoute">外层中继路由链。</param>
    /// <returns>确认写出的异步完成任务。</returns>
    private async Task HandlePolicyUpdateFaultAsync(IpcEnvelope envelope, string originKey, ChildMessageRoute? childRoute)
    {
        if (envelope.Id is long faultRequestId)
        {
            if (governance.TryGetPendingPolicyUpdateOperationKey(faultRequestId, out var pendingOperationKey)
                && pendingOperationKey is not null)
            {
                var opOwner = registry.GetOperationOwner(pendingOperationKey);
                if (!IsOriginAuthorizedForOperation(originKey, opOwner))
                {
                    callbackDispatcher.DispatchDiagnostic($"调用 '{originKey}' 无权回复操作 '{pendingOperationKey}' 的策略更新失败 (归属 '{opOwner}')。");
                    await AckChildNotificationAsync(childRoute, _activeSessionToken).ConfigureAwait(false);
                    return;
                }
            }

            governance.HandlePolicyFault(faultRequestId);
        }

        await AckChildNotificationAsync(childRoute, _activeSessionToken).ConfigureAwait(false);
    }
}
