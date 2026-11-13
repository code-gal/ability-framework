using System.Text.Json;
using System.Threading.Channels;
using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// 按条处理宿主动态策略更新，并区分本地拒绝与会话传输故障。
/// </summary>
internal sealed partial class HostExecutionCoordinator
{
    /// <summary>
    /// 持续消费策略更新通道；异常完成会驱动协调器受管断连。
    /// </summary>
    /// <param name="updates">策略更新通道读取端。</param>
    /// <param name="policyToken">策略工作循环取消令牌。</param>
    private async Task RunPolicyUpdatesAsync(
        ChannelReader<RunPolicyUpdate> updates,
        CancellationToken policyToken)
    {
        try
        {
            while (await updates.WaitToReadAsync(policyToken).ConfigureAwait(false))
            {
                while (updates.TryRead(out var update))
                {
                    if (policyToken.IsCancellationRequested
                        || sessionToken.IsCancellationRequested
                        || _disconnectCts.IsCancellationRequested)
                    {
                        return;
                    }

                    await ProcessPolicyUpdateAsync(update, policyToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (
            policyToken.IsCancellationRequested
            || sessionToken.IsCancellationRequested
            || _disconnectCts.IsCancellationRequested)
        {
            // 策略泵、会话或受管断连已请求结束，不属于通道故障。
        }
        catch (ChannelClosedException exception)
        {
            var failure = exception.InnerException ?? exception;
            callbackDispatcher.DispatchDiagnostic(
                $"动态策略更新通道异常关闭: {DescribeSafeError(failure)}");
            TriggerFaultDisconnect("PolicyUpdateChannelFault", failure);
        }
        catch (Exception exception)
        {
            callbackDispatcher.DispatchDiagnostic(
                $"动态策略更新循环异常: {DescribeSafeError(exception)}");
            TriggerFaultDisconnect("PolicyUpdateLoopFault", exception);
        }
    }

    /// <summary>
    /// 校验、登记并发送一条策略更新；本地拒绝只结束当前条目，传输故障则触发会话断连。
    /// </summary>
    /// <param name="update">从策略更新通道读取的请求。</param>
    /// <param name="policyToken">策略工作循环取消令牌。</param>
    private async Task ProcessPolicyUpdateAsync(RunPolicyUpdate update, CancellationToken policyToken)
    {
        if (!TryResolvePolicyTarget(update, out var operation))
        {
            return;
        }

        if (!RunPolicyBody.Validate(update.Policy, out _))
        {
            operation.RecordPolicyUpdateOutcome("Rejected", update.Version);
            callbackDispatcher.DispatchDiagnostic("动态策略更新内容不合法，已拒绝并继续处理后续更新。");
            return;
        }

        if (policyToken.IsCancellationRequested
            || sessionToken.IsCancellationRequested
            || _disconnectCts.IsCancellationRequested)
        {
            return;
        }

        if (connection.IsClosed)
        {
            var closedException = new IOException("动态策略更新时 IPC 连接已关闭。");
            callbackDispatcher.DispatchDiagnostic(
                $"动态策略更新无法发送: {DescribeSafeError(closedException)}");
            TriggerFaultDisconnect("PolicyUpdateTransportFault", closedException);
            return;
        }

        var requestId = connection.NextRequestId();
        if (!TryRegisterPolicyUpdate(requestId, update, operation, policyToken))
        {
            return;
        }

        if (!TryBuildPolicyEnvelope(requestId, update, out var targetEnvelope))
        {
            return;
        }

        await SendPolicyEnvelopeAsync(requestId, targetEnvelope, policyToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 解析策略更新的目标操作：更新为空、目标缺失或已结束时记录诊断并拒绝本条更新。
    /// </summary>
    /// <param name="update">从策略更新通道读取的请求。</param>
    /// <param name="operation">解析出的受管操作；失败时为空引用。</param>
    /// <returns>可以继续处理时为 true。</returns>
    private bool TryResolvePolicyTarget(RunPolicyUpdate update, out HostOperation operation)
    {
        operation = null!;
        if (update is null)
        {
            callbackDispatcher.DispatchDiagnostic("动态策略更新为空，已跳过。");
            return false;
        }

        if (string.IsNullOrWhiteSpace(update.OperationKey)
            || !governance.TryGetOperation(update.OperationKey, out var resolved)
            || resolved is null)
        {
            callbackDispatcher.DispatchDiagnostic("动态策略更新目标操作不存在，已跳过。");
            return false;
        }

        if (resolved.IsEnded)
        {
            callbackDispatcher.DispatchDiagnostic("动态策略更新目标操作已结束，已跳过。");
            return false;
        }

        operation = resolved;
        return true;
    }

    /// <summary>
    /// 本地登记未决策略更新：登记抛错或容量拒绝时终结本条记录，不影响会话。
    /// </summary>
    /// <param name="requestId">本次更新分配的请求编号。</param>
    /// <param name="update">策略更新请求。</param>
    /// <param name="operation">目标受管操作。</param>
    /// <param name="policyToken">策略工作循环取消令牌。</param>
    /// <returns>登记成功并可以发送时为 true。</returns>
    private bool TryRegisterPolicyUpdate(long requestId, RunPolicyUpdate update, HostOperation operation, CancellationToken policyToken)
    {
        bool registered;
        try
        {
            registered = governance.TryRegisterPolicyUpdate(
                requestId,
                update,
                options.PolicyUpdateTimeout,
                options.MaxPendingPolicyUpdates,
                options.TimeProvider,
                policyToken);
        }
        catch (Exception exception)
        {
            governance.HandlePolicySendFailure(requestId, "PolicySendRejected");
            callbackDispatcher.DispatchDiagnostic(
                $"动态策略更新未能本地登记: {DescribeSafeError(exception)}");
            return false;
        }

        if (registered)
        {
            return true;
        }

        operation.RecordPolicyUpdateOutcome("Rejected", update.Version);
        callbackDispatcher.DispatchDiagnostic(
            "动态策略更新登记被拒绝；操作状态已变化、策略无效或未决容量已满。");
        return false;
    }

    /// <summary>
    /// 构造策略更新信封，并按操作所属调用决定是否包装为子调用控制消息。
    /// </summary>
    /// <param name="requestId">本次更新分配的请求编号。</param>
    /// <param name="update">策略更新请求。</param>
    /// <param name="targetEnvelope">构造出的目标信封；失败时为空引用。</param>
    /// <returns>构造成功时为 true。</returns>
    private bool TryBuildPolicyEnvelope(long requestId, RunPolicyUpdate update, out IpcEnvelope targetEnvelope)
    {
        targetEnvelope = null!;
        try
        {
            var envelope = ProtocolMessages.CreatePolicyUpdate(
                requestId,
                update.OperationKey,
                update.Version,
                update.Source,
                update.Policy.ToWireV3());
            var operationOwner = registry.GetOperationOwner(update.OperationKey);
            targetEnvelope = operationOwner is not null
                && !string.Equals(operationOwner, rootRecord.InvocationKey, StringComparison.Ordinal)
                    ? ProtocolMessages.CreateChildMessage(requestId, operationOwner, envelope)
                    : envelope;
            return true;
        }
        catch (Exception exception)
        {
            governance.HandlePolicySendFailure(requestId, "PolicySendRejected");
            var failureKind = IsPolicyEncodingFailure(exception) ? "本地编码拒绝" : "本地构造失败";
            callbackDispatcher.DispatchDiagnostic(
                $"动态策略更新{failureKind}: {DescribeSafeError(exception)}");
            return false;
        }
    }

    /// <summary>
    /// 在有限预算内发送策略更新：正常取消只终结本条记录，本地编码拒绝与传输故障分别处置。
    /// </summary>
    /// <param name="requestId">本次更新分配的请求编号。</param>
    /// <param name="targetEnvelope">待发送的目标信封。</param>
    /// <param name="policyToken">策略工作循环取消令牌。</param>
    /// <returns>发送的异步完成任务。</returns>
    private async Task SendPolicyEnvelopeAsync(long requestId, IpcEnvelope targetEnvelope, CancellationToken policyToken)
    {
        using var sendLimit = new CancellationTokenSource(DefaultSendTimeout, options.TimeProvider);
        using var linkedSend = CancellationTokenSource.CreateLinkedTokenSource(
            sendLimit.Token,
            policyToken,
            sessionToken,
            _disconnectCts.Token);

        try
        {
            await connection.SendAsync(targetEnvelope, linkedSend.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            policyToken.IsCancellationRequested
            || sessionToken.IsCancellationRequested
            || _disconnectCts.IsCancellationRequested)
        {
            // 正常取消只终结本条未决记录，不升级为发送故障。
            governance.HandlePolicySendFailure(requestId, "PolicySendCancelled");
        }
        catch (Exception exception) when (IsPolicyEncodingFailure(exception))
        {
            governance.HandlePolicySendFailure(requestId, "PolicySendRejected");
            callbackDispatcher.DispatchDiagnostic(
                $"动态策略更新本地编码拒绝: {DescribeSafeError(exception)}");
        }
        catch (OperationCanceledException exception) when (sendLimit.IsCancellationRequested)
        {
            governance.HandlePolicySendFailure(requestId, "PolicySendFailed");
            callbackDispatcher.DispatchDiagnostic(
                $"动态策略更新发送超时: {DescribeSafeError(exception)}");
            TriggerFaultDisconnect("PolicyUpdateSendTimeout", exception);
        }
        catch (Exception exception)
        {
            governance.HandlePolicySendFailure(requestId, "PolicySendFailed");
            callbackDispatcher.DispatchDiagnostic(
                $"动态策略更新传输失败: {DescribeSafeError(exception)}");
            TriggerFaultDisconnect("PolicyUpdateSendFault", exception);
        }
    }

    /// <summary>
    /// 判断发送失败是否来自本地消息构造或编码拒绝。
    /// </summary>
    /// <param name="exception">策略更新发送异常。</param>
    /// <returns>若异常属于本地编码拒绝则返回 true。</returns>
    private static bool IsPolicyEncodingFailure(Exception exception)
        => exception is PayloadTooLargeException
            or JsonException
            or ArgumentException
            or NotSupportedException;
}
