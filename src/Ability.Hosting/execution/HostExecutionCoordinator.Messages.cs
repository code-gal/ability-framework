using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// 宿主端执行协调器的入站消息分组处理分部。
/// 每帧先逐层解包并核验归属，再按结果协议、子调用、治理与操作、观测的顺序交给对应分组；
/// 分组只声明自己是否处理该帧，未处理的帧继续交给下一组，未知帧最后按原协议规则统一判定。
/// </summary>
internal sealed partial class HostExecutionCoordinator
{
    /// <summary>
    /// 单帧消息的处理处置结果。
    /// 本组未处理与已处理必须分开表达，否则会把“交给下一组”误当成“已接收”；结束主循环单独取值。
    /// </summary>
    private enum MessageDisposition
    {
        /// <summary>本组不识别该消息，交给下一分组继续判定。</summary>
        NotHandled,
        /// <summary>消息已处理完毕，主循环继续接收下一帧。</summary>
        Handled,
        /// <summary>消息已判定本次执行业务终态，结束主循环。</summary>
        EndLoop
    }

    /// <summary>
    /// 处理一帧入站消息：解包归属、吸收历史子调用消息、记录观测，并按分组顺序分派。
    /// </summary>
    /// <param name="envelope">入站信封；子调用包装时会被逐层替换为内层信封。</param>
    /// <returns>需要继续主循环时为 true；已判定终态时为 false。</returns>
    private async Task<bool> HandleInboundEnvelopeAsync(IpcEnvelope envelope)
    {
        var childRoute = UnwrapChildMessage(ref envelope, out var originKey, out var isHistorical);

        Func<IpcEnvelope, IpcEnvelope>? responseWrapper = childRoute is not null
            ? env => ChildMessageRouter.WrapChildResponse(childRoute, env)
            : null;

        if (isHistorical && !string.Equals(envelope.Type, ProtocolConstants.MessageTypes.ChildStop, StringComparison.Ordinal))
        {
            await AbsorbHistoricalChildMessageAsync(envelope, childRoute, responseWrapper, originKey).ConfigureAwait(false);
            return true;
        }

        RecordObservedMessage(_observedMessages, envelope.Type, ref _observedTruncated);

        var disposition = await HandleResultProtocolAsync(envelope).ConfigureAwait(false);
        if (disposition == MessageDisposition.NotHandled)
        {
            disposition = await HandleChildCallAsync(envelope, originKey, responseWrapper).ConfigureAwait(false);
        }

        if (disposition == MessageDisposition.NotHandled)
        {
            disposition = await HandleGovernanceMessageAsync(envelope, originKey, responseWrapper, childRoute).ConfigureAwait(false);
        }

        if (disposition == MessageDisposition.NotHandled)
        {
            disposition = await HandleObservationMessageAsync(envelope, originKey, childRoute).ConfigureAwait(false);
        }

        if (disposition == MessageDisposition.NotHandled)
        {
            disposition = await HandleUnknownMessageAsync(envelope, responseWrapper).ConfigureAwait(false);
        }

        return disposition != MessageDisposition.EndLoop;
    }

    /// <summary>
    /// 拆解多层嵌套的 <c>ChildMessage</c> 包装，逐层核验相邻父子归属并返回外层中继路由。
    /// 目标调用未命中活跃表时回退历史权威记录；历史标记一旦置位即保持。
    /// 解包后仍禁止子调用转发会话级结果帧。
    /// </summary>
    /// <param name="envelope">入站信封；逐层替换为内层信封。</param>
    /// <param name="originKey">内层消息的直接来源调用键。</param>
    /// <param name="isHistorical">来源调用是否只存在于历史权威记录。</param>
    /// <returns>外层中继路由链；非子调用消息时为 null。</returns>
    private ChildMessageRoute? UnwrapChildMessage(ref IpcEnvelope envelope, out string originKey, out bool isHistorical)
    {
        ChildMessageRoute? childRoute = null;
        var currentParentKey = rootRecord.InvocationKey;
        isHistorical = false;

        while (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.ChildMessage, StringComparison.Ordinal))
        {
            var outerRequestId = envelope.Id ?? 0;
            var wrapper = ProtocolMessages.ReadBody<ChildMessageBody>(envelope);
            if (!registry.TryGet(wrapper.InvocationKey, out var currentRecord) || currentRecord is null)
            {
                if (registry.TryGetHistorical(wrapper.InvocationKey, out currentRecord) && currentRecord is not null)
                {
                    isHistorical = true;
                }
                else
                {
                    throw new ProtocolException($"ChildMessage 声明的调用 '{wrapper.InvocationKey}' 不存在。");
                }
            }

            if (!string.Equals(currentRecord.ParentKey, currentParentKey, StringComparison.Ordinal))
            {
                throw new ProtocolException($"ChildMessage 包装层级违规：调用 '{wrapper.InvocationKey}' 的父调用为 '{currentRecord.ParentKey}'，而非当前外层调用 '{currentParentKey}'。");
            }

            childRoute = new ChildMessageRoute(outerRequestId, wrapper.InvocationKey, wrapper.Message?.Id ?? 0, childRoute);
            currentParentKey = wrapper.InvocationKey;
            if (wrapper.Message is null)
            {
                throw new ProtocolException("ChildMessage 缺少内层信封。");
            }

            envelope = wrapper.Message;
        }

        if (childRoute is not null && ChildMessageRouter.IsForbiddenForwardType(envelope.Type))
        {
            throw new ProtocolException($"子调用不得通过 ChildMessage 转发结果协议帧 '{envelope.Type}'。");
        }

        originKey = currentParentKey;
        return childRoute;
    }

    /// <summary>
    /// 吸收历史已退出子调用的迟到消息：迟到答复与通知幂等确认外层等待，
    /// 迟到双向新请求回发带内层原始编号的小型失败答复，绝不向答复帧回发故障帧。
    /// </summary>
    /// <param name="envelope">内层信封。</param>
    /// <param name="childRoute">外层中继路由链。</param>
    /// <param name="responseWrapper">可选的信封包装委托。</param>
    /// <param name="originKey">内层消息的直接来源调用键。</param>
    /// <returns>吸收完成的异步完成任务。</returns>
    private async Task AbsorbHistoricalChildMessageAsync(
        IpcEnvelope envelope,
        ChildMessageRoute? childRoute,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper,
        string originKey)
    {
        if (ChildMessageRouter.IsReplyType(envelope.Type) || envelope.Id is null or <= 0)
        {
            await AckChildNotificationAsync(childRoute, _activeSessionToken).ConfigureAwait(false);
            return;
        }

        var faultEnvelope = ProtocolMessages.CreateRequestFault(
            envelope.Id.Value,
            "ChildAlreadyExited",
            $"子调用 '{originKey}' 已退出，拒绝处理迟到请求。");
        await SendReplyAsync(faultEnvelope, responseWrapper, connection, _activeSessionToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 处理结果协议消息：一元结果提交、流式声明、流元素、流关闭与终态故障上报。
    /// 流关闭只声明进入对账阶段，实际关闭答复由主循环在消费排空后完成。
    /// </summary>
    /// <param name="envelope">内层信封。</param>
    /// <returns>本组处置结果。</returns>
    private async Task<MessageDisposition> HandleResultProtocolAsync(IpcEnvelope envelope)
    {
        if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.SubmitResult, StringComparison.Ordinal))
        {
            var submit = ProtocolMessages.ReadBody<SubmitResultBody>(envelope);
            ValidateDeclaredOutputType(submit.PayloadType, "技能提交的");

            _outputTypeName = submit.PayloadType;
            _outputJson = submit.Payload.Clone();
            _outcome = "SubmitResult";
            _businessOutcome = "SubmitResult";
            return MessageDisposition.EndLoop;
        }

        if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.ResultOpen, StringComparison.Ordinal))
        {
            var open = ProtocolMessages.ReadBody<ResultOpenBody>(envelope);
            ValidateDeclaredOutputType(open.PayloadType, "流式声明");

            _outputTypeName = open.PayloadType;
            return MessageDisposition.Handled;
        }

        if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.ResultItem, StringComparison.Ordinal))
        {
            var item = ProtocolMessages.ReadBody<ResultItemBody>(envelope);
            await _resultItemChannel!.Writer.WriteAsync(item, sessionToken).ConfigureAwait(false);
            return MessageDisposition.Handled;
        }

        if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.ResultClose, StringComparison.Ordinal))
        {
            _businessOutcome = "ResultClose";
            _resultItemChannel!.Writer.TryComplete();
            _isStreamClosing = true;
            _streamCloseTimeoutCts = new CancellationTokenSource(DefaultCallbackTimeout, options.TimeProvider);
            _streamCloseTimeoutTask = Task.Delay(Timeout.Infinite, _streamCloseTimeoutCts.Token);
            return MessageDisposition.Handled;
        }

        if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.SubmitFault, StringComparison.Ordinal))
        {
            var fault = ProtocolMessages.ReadBody<SubmitFaultBody>(envelope);
            _faultKind = fault.Kind;
            _faultMessage = fault.Message;
            _outcome = "Fault";
            _businessOutcome = "SubmitFault";
            return MessageDisposition.EndLoop;
        }

        return MessageDisposition.NotHandled;
    }

    /// <summary>
    /// 核验技能声明的输出类型与调用方预期类型严格一致，不一致时按协议错误终止。
    /// </summary>
    /// <param name="declaredType">技能声明的输出类型名。</param>
    /// <param name="declaredBy">声明来源前缀，用于保持原故障说明措辞。</param>
    private void ValidateDeclaredOutputType(string declaredType, string declaredBy)
    {
        if (string.IsNullOrWhiteSpace(invocation.OutputTypeName)
            || string.Equals(declaredType, invocation.OutputTypeName, StringComparison.Ordinal))
        {
            return;
        }

        _outcome = "ProtocolError";
        _faultKind = "OutputTypeMismatch";
        _faultMessage = $"{declaredBy}输出类型 '{declaredType}' 与调用方预期类型 '{invocation.OutputTypeName}' 不匹配。";
        throw new ProtocolException(_faultMessage);
    }

    /// <summary>
    /// 处理子调用组合控制消息：子调用启动与子调用停止。
    /// 两者都只做权限与容量裁决，实际停止或启动在受管后台任务中执行，不阻塞主接收循环。
    /// </summary>
    /// <param name="envelope">内层信封。</param>
    /// <param name="originKey">内层消息的直接来源调用键。</param>
    /// <param name="responseWrapper">可选的信封包装委托。</param>
    /// <returns>本组处置结果。</returns>
    private async Task<MessageDisposition> HandleChildCallAsync(
        IpcEnvelope envelope,
        string originKey,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper)
    {
        if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.ChildStart, StringComparison.Ordinal))
        {
            await HandleChildStartAsync(envelope, originKey, responseWrapper).ConfigureAwait(false);
            return MessageDisposition.Handled;
        }

        if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.ChildStop, StringComparison.Ordinal))
        {
            await HandleChildStopAsync(envelope, originKey, responseWrapper).ConfigureAwait(false);
            return MessageDisposition.Handled;
        }

        return MessageDisposition.NotHandled;
    }

    /// <summary>
    /// 核验子调用启动声明并登记启动任务；后台容量满载时明确回发 <c>ServerBusy</c> 拒绝答复。
    /// </summary>
    /// <param name="envelope">内层信封。</param>
    /// <param name="originKey">内层消息的直接来源调用键。</param>
    /// <param name="responseWrapper">可选的信封包装委托。</param>
    /// <returns>启动登记的异步完成任务。</returns>
    private async Task HandleChildStartAsync(
        IpcEnvelope envelope,
        string originKey,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper)
    {
        var requestId = envelope.Id ?? 0;
        var start = ProtocolMessages.ReadBody<ChildStartBody>(envelope);
        if (!string.Equals(start.ParentInvocationKey, originKey, StringComparison.Ordinal))
        {
            throw new ProtocolException("ChildStart 的父调用与消息来源不一致。");
        }

        using var registration = childCalls.Register(start);
        var childRecord = registration.Record;
        var started = TryStartLaunchTask(async () =>
        {
            try
            {
                var replyEnvelope = await childCalls.StartAsync(requestId, start, childRecord).ConfigureAwait(false);
                await SendReplyAsync(replyEnvelope, responseWrapper, connection, _activeSessionToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                callbackDispatcher.DispatchDiagnostic($"异步执行 ChildStart 异常: {DescribeSafeError(ex)}");
            }
        });

        if (started)
        {
            registration.TakeForLaunch();
            return;
        }

        registration.Dismiss();
        callbackDispatcher.DispatchDiagnostic("后台任务满载，拒绝调度 ChildStart。");
        await SendServerBusyReplyAsync(
            requestId,
            responseWrapper,
            "宿主后台任务池满载，拒绝调度 ChildStart。",
            "发送满载拒绝答复失败").ConfigureAwait(false);
    }

    /// <summary>
    /// 核验子调用停止目标归属并调度停止任务：活跃目标校验权限后标记子树停止，
    /// 历史已退出目标按权威退出记录幂等回发真实 <c>ChildReaped</c>，绝不重新授予资源。
    /// </summary>
    /// <param name="envelope">内层信封。</param>
    /// <param name="originKey">内层消息的直接来源调用键。</param>
    /// <param name="responseWrapper">可选的信封包装委托。</param>
    /// <returns>停止调度的异步完成任务。</returns>
    private async Task HandleChildStopAsync(
        IpcEnvelope envelope,
        string originKey,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper)
    {
        var requestId = envelope.Id ?? 0;
        var stop = ProtocolMessages.ReadBody<ChildStopBody>(envelope);
        if (!registry.TryGet(stop.InvocationKey, out var targetRecord) || targetRecord is null)
        {
            if (registry.TryGetHistorical(stop.InvocationKey, out var historicalRecord) && historicalRecord is not null)
            {
                ValidateStopTarget(historicalRecord, originKey);
                var reason = historicalRecord.State == InvocationState.Reaped ? "Reaped" : "Unresponsive";
                var reapedReply = ProtocolMessages.CreateChildReaped(
                    requestId,
                    stop.InvocationKey,
                    historicalRecord.ExitCode,
                    0,
                    reason,
                    historicalRecord.ExitedNaturally ?? false);
                await SendReplyAsync(reapedReply, responseWrapper, connection, _activeSessionToken).ConfigureAwait(false);
                return;
            }

            throw new ProtocolException($"ChildStop 目标调用 '{stop.InvocationKey}' 不存在。");
        }

        ValidateStopTarget(targetRecord, originKey);

        var subtree = registry.RequestStopSubtree(stop.InvocationKey);
        if (TryScheduleChildStop(requestId, stop.InvocationKey, subtree, responseWrapper, connection, _activeSessionToken))
        {
            return;
        }

        callbackDispatcher.DispatchDiagnostic("后台控制任务满载，拒绝调度 ChildStop。");
        await SendServerBusyReplyAsync(
            requestId,
            responseWrapper,
            "宿主后台任务池满载，拒绝调度 ChildStop。",
            "发送 ChildStop 满载拒绝答复失败").ConfigureAwait(false);
    }

    /// <summary>
    /// 核验停止目标：禁止停止根调用，且只允许来源调用停止其所属子树内的后代。
    /// </summary>
    /// <param name="targetRecord">停止目标的权威记录。</param>
    /// <param name="originKey">发起停止的调用键。</param>
    private void ValidateStopTarget(InvocationRecord targetRecord, string originKey)
    {
        if (string.Equals(targetRecord.InvocationKey, rootRecord.InvocationKey, StringComparison.Ordinal))
        {
            throw new ProtocolException("禁止对根调用执行 ChildStop。");
        }

        if (!ChildMessageRouter.IsDescendantOf(registry, targetRecord, originKey))
        {
            throw new ProtocolException($"调用 '{originKey}' 无权停止非后代调用 '{targetRecord.InvocationKey}'。");
        }
    }

    /// <summary>
    /// 向发起方回发后台容量满载的小型故障答复；发送故障只记诊断，不影响已完成的容量裁决。
    /// </summary>
    /// <param name="requestId">待答复请求编号。</param>
    /// <param name="responseWrapper">可选的信封包装委托。</param>
    /// <param name="reason">满载原因说明。</param>
    /// <param name="diagnosticLabel">发送失败时的诊断前缀。</param>
    /// <returns>写出答复的异步完成任务。</returns>
    private async Task SendServerBusyReplyAsync(
        long requestId,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper,
        string reason,
        string diagnosticLabel)
    {
        var faultEnvelope = ProtocolMessages.CreateRequestFault(requestId, "ServerBusy", reason);
        try
        {
            await SendReplyAsync(faultEnvelope, responseWrapper, connection, _activeSessionToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            callbackDispatcher.DispatchDiagnostic($"{diagnosticLabel}: {DescribeSafeError(ex)}");
        }
    }

    /// <summary>
    /// 处理观测类入站消息：进度通知在操作状态机采纳后派发操作快照与宿主进度投影。
    /// 非空操作键必须先核验归属，非空操作进度只在采纳后对外派发，子调用转发仍确认外层代理请求。
    /// </summary>
    /// <param name="envelope">内层信封。</param>
    /// <param name="originKey">内层消息的直接来源调用键。</param>
    /// <param name="childRoute">外层中继路由链。</param>
    /// <returns>本组处置结果。</returns>
    private async Task<MessageDisposition> HandleObservationMessageAsync(
        IpcEnvelope envelope,
        string originKey,
        ChildMessageRoute? childRoute)
    {
        if (!string.Equals(envelope.Type, ProtocolConstants.MessageTypes.Progress, StringComparison.Ordinal))
        {
            return MessageDisposition.NotHandled;
        }

        var progress = ProtocolMessages.ReadBody<ProgressBody>(envelope);

        if (!string.IsNullOrWhiteSpace(progress.OperationKey))
        {
            var opOwner = registry.GetOperationOwner(progress.OperationKey);
            if (!IsOriginAuthorizedForOperation(originKey, opOwner))
            {
                callbackDispatcher.DispatchDiagnostic($"调用 '{originKey}' 无权发送操作 '{progress.OperationKey}' 的进度通知 (归属 '{opOwner}')。");
                await AckChildNotificationAsync(childRoute, _activeSessionToken).ConfigureAwait(false);
                return MessageDisposition.Handled;
            }

            if (!governance.TryGetOperation(progress.OperationKey, out var op)
                || op is null
                || !op.ApplyProgress(progress))
            {
                await AckChildNotificationAsync(childRoute, _activeSessionToken).ConfigureAwait(false);
                return MessageDisposition.Handled;
            }

            callbackDispatcher.DispatchSnapshot(op.Snapshot());
        }

        callbackDispatcher.DispatchOperationProgress(progress);
        DispatchHostProgress(progress);
        await AckChildNotificationAsync(childRoute, _activeSessionToken).ConfigureAwait(false);
        return MessageDisposition.Handled;
    }

    /// <summary>
    /// 把协议进度投影为宿主进度回调；回调失败不影响控制消息分发与业务结果。
    /// </summary>
    /// <param name="progress">入站进度通知。</param>
    private void DispatchHostProgress(ProgressBody progress)
    {
        if (options.ProgressCallback is null)
        {
            return;
        }

        try
        {
            double? percent = progress.Completed is { } completed && progress.Total is { } total && total > 0
                ? (double)completed / total
                : null;
            var message = !string.IsNullOrEmpty(progress.Message) ? progress.Message : progress.Stage;

            callbackDispatcher.DispatchHostProgress(new HostProgress(progress.OperationKey, progress.Sequence, percent, message, null));
        }
        catch
        {
            // 宿主进度投影失败按观测隔离处理，不改变已接受的结果与控制状态。
        }
    }

    /// <summary>
    /// 处理各组均未识别的消息：带请求编号的未知请求回发 <c>UnsupportedRequest</c>；
    /// 可选通知安全忽略；未识别的必选通知判为协议违规。
    /// </summary>
    /// <param name="envelope">内层信封。</param>
    /// <param name="responseWrapper">可选的信封包装委托。</param>
    /// <returns>本组处置结果；必选通知违规时抛出协议异常。</returns>
    private async Task<MessageDisposition> HandleUnknownMessageAsync(
        IpcEnvelope envelope,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper)
    {
        if (envelope.Id is long requestId)
        {
            var fault = ProtocolMessages.CreateRequestFault(
                requestId,
                "UnsupportedRequest",
                $"Host does not support request type '{envelope.Type}'.");
            await SendReplyAsync(fault, responseWrapper, connection, _activeSessionToken).ConfigureAwait(false);
            return MessageDisposition.Handled;
        }

        if (envelope.OptionalNotification)
        {
            return MessageDisposition.Handled;
        }

        _outcome = "ProtocolError";
        _faultKind = "UnexpectedNotification";
        _faultMessage = $"宿主收到未识别的必选协议通知 '{envelope.Type}'。";
        throw new ProtocolException(_faultMessage);
    }
}
