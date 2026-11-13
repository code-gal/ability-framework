using System.Text.Json;
using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// 治理协调器的补参审批分部：Schema 校验器解析、答复形态核验与有界载荷校验。
/// </summary>
internal sealed partial class HostGovernanceCoordinator
{
    /// <summary>
    /// 调度并在独立后台任务中执行参数补充决策。
    /// 复用 Core 结构与字节大小校验，构造失败或超限回发小型 RequestFault。
    /// </summary>
    /// <param name="requestId">请求标识编号。</param>
    /// <param name="request">补参申请数据体。</param>
    /// <param name="callback">外部注入的补参决策回调委托。</param>
    /// <param name="connection">IPC 双工通信连接。</param>
    /// <param name="timeout">最大决策等待超时时限。</param>
    /// <param name="timeProvider">时间提供者。</param>
    /// <param name="sessionToken">会话级取消令牌。</param>
    /// <param name="validator">针对参数值的 Schema 校验器（可选，为空且申请含 Schema 时自动解析）。</param>
    /// <param name="responseWrapper">可选的外层信封包装器（用于子调用消息回复转发）。</param>
    /// <param name="originalRequestId">客户端原始内层请求编号（若与 requestId 隔离时透传）。</param>
    public void ScheduleParamDecision(
        long requestId,
        ParamRequestBody request,
        Func<long, ParamRequestBody, CancellationToken, ValueTask<ParamDecision?>>? callback,
        IpcConnection connection,
        TimeSpan? timeout,
        TimeProvider timeProvider,
        CancellationToken sessionToken,
        JsonSchemaValidator? validator = null,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper = null,
        long? originalRequestId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(connection);

        var replyReqId = originalRequestId ?? requestId;

        if (!TryResolveParamValidator(connection, request, replyReqId, sessionToken, responseWrapper, ref validator))
        {
            return;
        }

        ScheduleDecision<ParamDecision>(
            requestId,
            "Param",
            connection,
            timeout,
            timeProvider,
            sessionToken,
            callback is not null ? ct => callback(replyReqId, request, ct) : null,
            decision => Task.FromResult(BuildParamReplyEnvelope(decision, request, replyReqId, validator)),
            responseWrapper,
            originalRequestId);
    }

    /// <summary>
    /// 解析补参请求的 Schema 校验器；Schema 不受支持或解析失败时直接回复故障并返回 false。
    /// </summary>
    /// <param name="connection">根 IPC 连接。</param>
    /// <param name="request">补参请求体。</param>
    /// <param name="replyReqId">答复使用的客户端请求编号。</param>
    /// <param name="sessionToken">会话级取消令牌。</param>
    /// <param name="responseWrapper">可选的外层信封包装器。</param>
    /// <param name="validator">已提供的校验器；解析成功后回填。</param>
    /// <returns>可以继续调度决策时为 true。</returns>
    private bool TryResolveParamValidator(
        IpcConnection connection,
        ParamRequestBody request,
        long replyReqId,
        CancellationToken sessionToken,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper,
        ref JsonSchemaValidator? validator)
    {
        if (validator is not null || request.Schema is not { } schema)
        {
            return true;
        }

        try
        {
            validator = JsonSchemaValidator.For(schema);
            return true;
        }
        catch (SchemaNotSupportedException ex)
        {
            var fault = ProtocolMessages.CreateRequestFault(replyReqId, "SchemaNotSupported", $"请求指定的 Schema 不受支持: {DescribeSafeError(ex)}");
            StartDeliveryWorker(() => DeliverReplyOrCloseAsync(connection, fault, responseWrapper, sessionToken));
            return false;
        }
        catch (Exception ex)
        {
            var fault = ProtocolMessages.CreateRequestFault(replyReqId, "SchemaNotSupported", $"解析请求 Schema 失败: {DescribeSafeError(ex)}");
            StartDeliveryWorker(() => DeliverReplyOrCloseAsync(connection, fault, responseWrapper, sessionToken));
            return false;
        }
    }

    /// <summary>
    /// 按请求形态构造补参答复信封：结构化申请走载荷互斥与类型匹配，纯文本申请要求有效文本值。
    /// </summary>
    /// <param name="decision">决策回调返回值；为 null 时保持有效等待。</param>
    /// <param name="request">补参请求体。</param>
    /// <param name="replyReqId">答复使用的客户端请求编号。</param>
    /// <param name="validator">Schema 校验器；为空时跳过校验。</param>
    /// <returns>待交付的答复信封，或 null。</returns>
    private IpcEnvelope? BuildParamReplyEnvelope(
        ParamDecision? decision,
        ParamRequestBody request,
        long replyReqId,
        JsonSchemaValidator? validator)
    {
        if (decision is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(request.PayloadType)
            ? BuildTextParamReply(decision, replyReqId, validator)
            : BuildStructuredParamReply(decision, request.PayloadType, replyReqId, validator);
    }

    /// <summary>
    /// 构造结构化补参答复：核验 Payload 与 Value 互斥、类型匹配，并施加有界编码校验与 Schema 校验。
    /// </summary>
    /// <param name="decision">决策回调返回值。</param>
    /// <param name="requestedPayloadType">请求声明的载荷类型名。</param>
    /// <param name="replyReqId">答复使用的客户端请求编号。</param>
    /// <param name="validator">Schema 校验器；为空时跳过校验。</param>
    /// <returns>待交付的答复信封。</returns>
    private IpcEnvelope BuildStructuredParamReply(
        ParamDecision decision,
        string requestedPayloadType,
        long replyReqId,
        JsonSchemaValidator? validator)
    {
        if (decision.Payload is not { } structuredPayload || decision.Value is not null || string.IsNullOrWhiteSpace(decision.PayloadType))
        {
            return ProtocolMessages.CreateRequestFault(replyReqId, "InvalidParamValue", "结构化补参答复缺少有效 Payload 或违反互斥约束。");
        }

        if (!string.Equals(decision.PayloadType, requestedPayloadType, StringComparison.Ordinal))
        {
            return ProtocolMessages.CreateRequestFault(replyReqId, "InvalidParamValue", $"结构化补参答复类型 '{decision.PayloadType}' 与请求 '{requestedPayloadType}' 不匹配。");
        }

        try
        {
            IpcFrame.ValidateJsonElementBounded(structuredPayload, MaxParamPayloadBytes, ProtocolConstants.MaxEncodeWorkspaceBytes, "ParamValue");
        }
        catch (PayloadTooLargeException)
        {
            return ProtocolMessages.CreateRequestFault(replyReqId, "PayloadTooLarge", "结构化补参载荷超出协议允许的最大大小。");
        }
        catch (Exception ex)
        {
            return ProtocolMessages.CreateRequestFault(replyReqId, "SerializationFailure", $"结构化补参载荷编码失败: {DescribeSafeError(ex)}");
        }

        if (validator is null)
        {
            return ProtocolMessages.CreateParamPayloadValue(replyReqId, decision.PayloadType, structuredPayload);
        }

        try
        {
            var validation = validator.Validate(structuredPayload);
            return validation.IsValid
                ? ProtocolMessages.CreateParamPayloadValue(replyReqId, decision.PayloadType, structuredPayload)
                : ProtocolMessages.CreateRequestFault(replyReqId, "ParamSchemaViolation", validation.Summary);
        }
        catch (SchemaValidationIncompleteException ex)
        {
            return ProtocolMessages.CreateRequestFault(replyReqId, "ParamValidationIncomplete", DescribeSafeError(ex));
        }
    }

    /// <summary>
    /// 构造文本补参答复：核验互斥约束，并施加有界编码校验与 Schema 校验。
    /// </summary>
    /// <param name="decision">决策回调返回值。</param>
    /// <param name="replyReqId">答复使用的客户端请求编号。</param>
    /// <param name="validator">Schema 校验器；为空时跳过校验。</param>
    /// <returns>待交付的答复信封。</returns>
    private IpcEnvelope BuildTextParamReply(ParamDecision decision, long replyReqId, JsonSchemaValidator? validator)
    {
        if (decision.Value is null || decision.Payload is not null || !string.IsNullOrWhiteSpace(decision.PayloadType))
        {
            return ProtocolMessages.CreateRequestFault(replyReqId, "InvalidParamValue", "文本补参答复缺少有效文本值或包含了非预期的结构化载荷。");
        }

        try
        {
            IpcFrame.ValidateStringPayloadBounded(decision.Value, MaxParamPayloadBytes, ProtocolConstants.MaxEncodeWorkspaceBytes, "ParamValue");
        }
        catch (PayloadTooLargeException ex)
        {
            RecordIssue("ParamValidation", ex);
            return ProtocolMessages.CreateRequestFault(replyReqId, "PayloadTooLarge", "文本补参值超出协议允许的最大大小。");
        }
        catch (Exception ex)
        {
            RecordIssue("ParamSerialization", ex);
            return ProtocolMessages.CreateRequestFault(replyReqId, "SerializationFailure", $"文本补参编码失败: {DescribeSafeError(ex)}");
        }

        if (validator is null)
        {
            return ProtocolMessages.CreateParamValue(replyReqId, decision.Value);
        }

        try
        {
            var element = JsonSerializer.SerializeToElement(decision.Value, HostGovernanceJsonContext.Default.String);
            var validation = validator.Validate(element);
            return validation.IsValid
                ? ProtocolMessages.CreateParamValue(replyReqId, decision.Value)
                : ProtocolMessages.CreateRequestFault(replyReqId, "ParamSchemaViolation", validation.Summary);
        }
        catch (SchemaValidationIncompleteException ex)
        {
            RecordIssue("ParamValidationIncomplete", ex);
            return ProtocolMessages.CreateRequestFault(replyReqId, "ParamValidationIncomplete", DescribeSafeError(ex));
        }
    }

}
