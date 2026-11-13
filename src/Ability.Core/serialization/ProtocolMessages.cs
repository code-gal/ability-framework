using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Ability.Core;

/// <summary>
/// 集中创建和解析已支持的协议消息体，封装内部源生成元数据，使外部调用方无须依赖私有序列化上下文。
/// </summary>
public static class ProtocolMessages
{
    /// <summary>
    /// 当前协议主版本号。
    /// </summary>
    public const int CurrentProtocolMajor = ProtocolConstants.CurrentProtocolMajor;

    /// <summary>
    /// 创建握手请求信封。
    /// </summary>
    /// <param name="resultCreditWindow">流式结果初始信用额度窗口。</param>
    /// <param name="cleanupGracePeriod">退出清理宽限期。</param>
    /// <param name="buildTimeout">构建与准备超时；若非正数则默认 2 分钟。</param>
    /// <param name="connectionTimeout">连接建立超时；若非正数则默认 5 秒。</param>
    public static IpcEnvelope CreateHello(
        int resultCreditWindow,
        TimeSpan cleanupGracePeriod,
        TimeSpan buildTimeout = default,
        TimeSpan connectionTimeout = default)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.Hello,
            null,
            new HelloBody(
                CurrentProtocolMajor,
                resultCreditWindow,
                cleanupGracePeriod,
                buildTimeout > TimeSpan.Zero ? buildTimeout : TimeSpan.FromMinutes(2),
                connectionTimeout > TimeSpan.Zero ? connectionTimeout : TimeSpan.FromSeconds(5)),
            ProtocolJsonContext.Default.HelloBody);

    /// <summary>
    /// 创建握手接受确认信封，回显确认支持的协议主版本。
    /// </summary>
    public static IpcEnvelope CreateHelloAccepted()
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.HelloAccepted,
            null,
            new HelloAcceptedBody(CurrentProtocolMajor),
            ProtocolJsonContext.Default.HelloAcceptedBody);

    /// <summary>
    /// 创建协议版本不匹配拒绝信封。
    /// </summary>
    /// <param name="expectedMajor">期望支持的协议主版本。</param>
    /// <param name="actualMajor">对端实际提出的协议主版本。</param>
    public static IpcEnvelope CreateProtocolMismatch(int expectedMajor, int actualMajor)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.ProtocolMismatch,
            null,
            new ProtocolMismatchBody(expectedMajor, actualMajor),
            ProtocolJsonContext.Default.ProtocolMismatchBody);

    /// <summary>
    /// 创建明确标记为可安全忽略的扩展通知信封；仅允许无请求 ID 的通知消息。
    /// </summary>
    /// <param name="type">消息类型标识。</param>
    /// <param name="payload">可选的业务载荷。</param>
    public static IpcEnvelope CreateOptionalNotification(string type, JsonElement? payload = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        return new IpcEnvelope { Type = type, Payload = payload, OptionalNotification = true };
    }

    /// <summary>
    /// 创建首帧任务输入信封。
    /// </summary>
    /// <param name="taskId">所属任务 ID。</param>
    /// <param name="taskTempPath">宿主分配的专属临时目录绝对路径。</param>
    /// <param name="payloadType">业务输入类型名提示。</param>
    /// <param name="payload">原始 JSON 形式的输入载荷。</param>
    /// <param name="traceParent">可选的分布式追踪上下文 traceparent。</param>
    /// <param name="traceState">可选的分布式追踪状态 tracestate。</param>
    public static IpcEnvelope CreateTaskInput(
        string taskId,
        string taskTempPath,
        string payloadType,
        JsonElement payload,
        string? traceParent = null,
        string? traceState = null)
        => new()
        {
            Type = ProtocolConstants.MessageTypes.TaskInput,
            Payload = JsonSerializer.SerializeToElement(
                new TaskInputBody(taskId, taskTempPath, payloadType, payload),
                ProtocolJsonContext.Default.TaskInputBody),
            TraceParent = traceParent,
            TraceState = traceState
        };

    /// <summary>
    /// 创建一元结果提交终态信封。
    /// </summary>
    /// <param name="payloadType">结果类型名。</param>
    /// <param name="payload">以原始 JSON 表达的业务结果。</param>
    public static IpcEnvelope CreateSubmitResult(string payloadType, JsonElement payload)
        => new()
        {
            Type = ProtocolConstants.MessageTypes.SubmitResult,
            Payload = JsonSerializer.SerializeToElement(
                new SubmitResultBody(payloadType, payload),
                ProtocolJsonContext.Default.SubmitResultBody)
        };

    /// <summary>
    /// 创建终态故障上报信封；各文本字段在序列化前按 UTF-8 字节截断，防止撑大信封。
    /// </summary>
    /// <param name="kind">故障类别简短标识（截断至 256 字节）。</param>
    /// <param name="message">故障说明文本（截断至 4096 字节）。</param>
    /// <param name="site">发生位置标识（截断至 256 字节）。</param>
    public static IpcEnvelope CreateSubmitFault(string kind, string message, string site)
        => new()
        {
            Type = ProtocolConstants.MessageTypes.SubmitFault,
            Payload = JsonSerializer.SerializeToElement(
                new SubmitFaultBody(
                    LimitUtf8(kind, 256),
                    LimitUtf8(message, 4096),
                    LimitUtf8(site, 256)),
                ProtocolJsonContext.Default.SubmitFaultBody)
        };

    /// <summary>
    /// 创建对指定请求的失败答复信封；带回原请求 ID。
    /// </summary>
    /// <param name="id">被答复的原请求 ID。</param>
    /// <param name="kind">错误类型标识（截断至 128 字节）。</param>
    /// <param name="message">错误说明（截断至 2048 字节）。</param>
    public static IpcEnvelope CreateRequestFault(long id, string kind, string message)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.RequestFault,
            id,
            new RequestFaultBody(LimitUtf8(kind, 128), LimitUtf8(message, 2048)),
            ProtocolJsonContext.Default.RequestFaultBody);

    /// <summary>
    /// 创建子调用启动请求信封。
    /// </summary>
    public static IpcEnvelope CreateChildStart(
        long requestId,
        string invocationKey,
        string parentInvocationKey,
        string taskId,
        string launcherPath,
        string? traceParent = null,
        string? traceState = null)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.ChildStart,
            requestId,
            new ChildStartBody(invocationKey, parentInvocationKey, taskId, launcherPath),
            ProtocolJsonContext.Default.ChildStartBody,
            traceParent,
            traceState);

    /// <summary>
    /// 创建子进程已启动确认信封。
    /// </summary>
    public static IpcEnvelope CreateChildStarted(
        long requestId,
        string invocationKey,
        string taskId,
        int processId)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.ChildStarted,
            requestId,
            new ChildStartedBody(invocationKey, taskId, processId),
            ProtocolJsonContext.Default.ChildStartedBody);

    /// <summary>
    /// 创建请求停止子进程信封。
    /// </summary>
    public static IpcEnvelope CreateChildStop(
        long requestId,
        string invocationKey,
        string parentInvocationKey,
        string reason)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.ChildStop,
            requestId,
            new ChildStopBody(invocationKey, parentInvocationKey, reason),
            ProtocolJsonContext.Default.ChildStopBody);

    /// <summary>
    /// 创建子进程已回收通知信封。
    /// </summary>
    public static IpcEnvelope CreateChildReaped(
        long requestId,
        string invocationKey,
        int? exitCode,
        int cleanedLeaseCount,
        string outcome,
        bool exitedNaturally = false)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.ChildReaped,
            requestId,
            new ChildReapedBody(invocationKey, exitCode, cleanedLeaseCount, outcome, exitedNaturally),
            ProtocolJsonContext.Default.ChildReapedBody);

    /// <summary>
    /// 创建转发给子调用的消息信封。
    /// </summary>
    public static IpcEnvelope CreateChildMessage(long requestId, string invocationKey, IpcEnvelope message)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.ChildMessage,
            requestId,
            new ChildMessageBody(invocationKey, message),
            ProtocolJsonContext.Default.ChildMessageBody);

    /// <summary>
    /// 创建转发给子调用的控制通知信封（无外部请求 ID）。
    /// </summary>
    public static IpcEnvelope CreateChildControl(string invocationKey, IpcEnvelope message)
        => new()
        {
            Type = ProtocolConstants.MessageTypes.ChildMessage,
            Payload = JsonSerializer.SerializeToElement(
                new ChildMessageBody(invocationKey, message),
                ProtocolJsonContext.Default.ChildMessageBody)
        };

    /// <summary>
    /// 创建转发子调用消息的答复信封。
    /// </summary>
    public static IpcEnvelope CreateChildMessageReply(
        long requestId,
        string invocationKey,
        IpcEnvelope? response)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.ChildMessageReply,
            requestId,
            new ChildMessageReplyBody(invocationKey, response),
            ProtocolJsonContext.Default.ChildMessageReplyBody);

    /// <summary>
    /// 创建受管资源租约申请信封。
    /// </summary>
    public static IpcEnvelope CreateLeaseRequest(long id, string resource, string mode)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.LeaseRequest,
            id,
            new LeaseRequestBody(resource, mode),
            ProtocolJsonContext.Default.LeaseRequestBody);

    /// <summary>
    /// 创建受管资源租约批准答复信封。
    /// </summary>
    public static IpcEnvelope CreateLeaseGranted(long id, string leaseId)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.LeaseGranted,
            id,
            new LeaseGrantedBody(leaseId),
            ProtocolJsonContext.Default.LeaseGrantedBody);

    /// <summary>
    /// 创建受管资源租约拒绝答复信封。
    /// </summary>
    public static IpcEnvelope CreateLeaseDenied(long id, string reason)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.LeaseDenied,
            id,
            new LeaseDeniedBody(reason),
            ProtocolJsonContext.Default.LeaseDeniedBody);

    /// <summary>
    /// 创建受管资源租约释放通知信封。
    /// </summary>
    public static IpcEnvelope CreateLeaseRelease(string leaseId)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.LeaseRelease,
            null,
            new LeaseReleaseBody(leaseId),
            ProtocolJsonContext.Default.LeaseReleaseBody);

    /// <summary>
    /// 创建参数补充申请信封。
    /// </summary>
    public static IpcEnvelope CreateParamRequest(
        long id,
        string key,
        string hint,
        string? payloadType = null,
        JsonElement? schema = null,
        JsonElement? uiDescriptor = null)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.ParamRequest,
            id,
            new ParamRequestBody(key, hint, payloadType, schema, uiDescriptor),
            ProtocolJsonContext.Default.ParamRequestBody);

    /// <summary>
    /// 创建文本参数补充交付答复信封。
    /// </summary>
    public static IpcEnvelope CreateParamValue(long id, string value)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.ParamValue,
            id,
            new ParamValueBody(value),
            ProtocolJsonContext.Default.ParamValueBody);

    /// <summary>
    /// 创建结构化参数补充交付答复信封。
    /// </summary>
    public static IpcEnvelope CreateParamPayloadValue(long id, string payloadType, JsonElement payload)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.ParamValue,
            id,
            new ParamValueBody(null, payloadType, payload),
            ProtocolJsonContext.Default.ParamValueBody);

    /// <summary>
    /// 创建数据源恢复请求信封。
    /// </summary>
    public static IpcEnvelope CreateSourceRepair(long id, SourceRepairBody body)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.SourceRepair,
            id,
            body,
            ProtocolJsonContext.Default.SourceRepairBody);

    /// <summary>
    /// 创建数据源恢复结果答复信封。
    /// </summary>
    public static IpcEnvelope CreateSourceRepairResult(long id, string outcome)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.SourceRepairResult,
            id,
            new SourceRepairResultBody(outcome),
            ProtocolJsonContext.Default.SourceRepairResultBody);

    /// <summary>
    /// 创建协同取消通知信封。
    /// </summary>
    public static IpcEnvelope CreateCancel()
        => new() { Type = ProtocolConstants.MessageTypes.Cancel };

    /// <summary>
    /// 创建会话进程安全退出通知信封。
    /// </summary>
    public static IpcEnvelope CreateShutdown()
        => new() { Type = ProtocolConstants.MessageTypes.Shutdown };

    /// <summary>
    /// 创建开启流式结果声明信封。
    /// </summary>
    public static IpcEnvelope CreateResultOpen(string payloadType)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.ResultOpen,
            null,
            new ResultOpenBody(payloadType),
            ProtocolJsonContext.Default.ResultOpenBody);

    /// <summary>
    /// 创建流式结果元素信封。
    /// </summary>
    public static IpcEnvelope CreateResultItem(JsonElement payload)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.ResultItem,
            null,
            new ResultItemBody(payload),
            ProtocolJsonContext.Default.ResultItemBody);

    /// <summary>
    /// 创建关闭结果流终态信封。
    /// </summary>
    public static IpcEnvelope CreateResultClose()
        => new() { Type = ProtocolConstants.MessageTypes.ResultClose };

    /// <summary>
    /// 创建流结果信用额度发放信封。
    /// </summary>
    public static IpcEnvelope CreateResultCredit(int count)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.ResultCredit,
            null,
            new ResultCreditBody(count),
            ProtocolJsonContext.Default.ResultCreditBody);

    /// <summary>
    /// 创建结果流结束汇总确认信封。
    /// </summary>
    public static IpcEnvelope CreateResultClosed(int itemCount)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.ResultClosed,
            null,
            new ResultClosedBody(itemCount),
            ProtocolJsonContext.Default.ResultClosedBody);

    /// <summary>
    /// 创建受管操作登记请求信封。
    /// </summary>
    public static IpcEnvelope CreateOperationRegister(
        long requestId,
        string operationKey,
        string? parentOperationKey,
        string name,
        string purpose,
        RunPolicyBody suggestedPolicy)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.OperationRegister,
            requestId,
            new OperationRegisterBody(
                operationKey,
                parentOperationKey,
                name,
                purpose,
                suggestedPolicy),
            ProtocolJsonContext.Default.OperationRegisterBody);

    /// <summary>
    /// 创建受管操作登记确认答复信封。
    /// </summary>
    public static IpcEnvelope CreateOperationAccepted(
        long requestId,
        string operationKey,
        long policyVersion,
        string policySource,
        RunPolicyBody policy)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.OperationAccepted,
            requestId,
            new OperationAcceptedBody(operationKey, policyVersion, policySource, policy),
            ProtocolJsonContext.Default.OperationAcceptedBody);

    /// <summary>
    /// 创建受管操作运行策略动态更新信封。
    /// </summary>
    public static IpcEnvelope CreatePolicyUpdate(
        long requestId,
        string operationKey,
        long version,
        string source,
        RunPolicyBody policy)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.PolicyUpdate,
            requestId,
            new PolicyUpdateBody(operationKey, version, source, policy),
            ProtocolJsonContext.Default.PolicyUpdateBody);

    /// <summary>
    /// 创建受管操作策略应用确认答复信封。
    /// </summary>
    public static IpcEnvelope CreatePolicyApplied(
        long requestId,
        string operationKey,
        long version,
        string source,
        string outcome)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.PolicyApplied,
            requestId,
            new PolicyAppliedBody(operationKey, version, source, outcome),
            ProtocolJsonContext.Default.PolicyAppliedBody);

    /// <summary>
    /// 创建指定消息类型的受管操作状态通知信封。
    /// </summary>
    public static IpcEnvelope CreateOperationState(
        string type,
        string operationKey,
        int attempts,
        string state,
        string? reason = null)
        => CreateBodyMessage(
            type,
            null,
            new OperationStateBody(operationKey, attempts, state, reason),
            ProtocolJsonContext.Default.OperationStateBody);

    /// <summary>
    /// 创建受管操作常规状态通知信封（类型为 OperationState）。
    /// </summary>
    public static IpcEnvelope CreateOperationState(
        string operationKey,
        int attempts,
        string state,
        string? reason = null)
        => CreateOperationState(
            ProtocolConstants.MessageTypes.OperationState,
            operationKey,
            attempts,
            state,
            reason);

    /// <summary>
    /// 创建受管操作尝试开始通知信封（类型为 OperationStarted）。
    /// </summary>
    public static IpcEnvelope CreateOperationStarted(
        string operationKey,
        int attempts,
        string state = "Running")
        => CreateOperationState(
            ProtocolConstants.MessageTypes.OperationStarted,
            operationKey,
            attempts,
            state,
            null);

    /// <summary>
    /// 创建受管操作尝试结束通知信封（类型为 AttemptEnded）。
    /// </summary>
    public static IpcEnvelope CreateAttemptEnded(
        string operationKey,
        int attempts,
        string state,
        string? reason = null)
        => CreateOperationState(
            ProtocolConstants.MessageTypes.AttemptEnded,
            operationKey,
            attempts,
            state,
            reason);

    /// <summary>
    /// 创建受管操作整体生命周期结束通知信封（类型为 OperationEnd）。
    /// </summary>
    public static IpcEnvelope CreateOperationEnd(
        string operationKey,
        int attempts,
        string state,
        string? reason = null)
        => CreateOperationState(
            ProtocolConstants.MessageTypes.OperationEnd,
            operationKey,
            attempts,
            state,
            reason);

    /// <summary>
    /// 创建受管操作超时到期通知信封。
    /// </summary>
    public static IpcEnvelope CreateOperationExpired(
        string operationKey,
        long policyVersion,
        string source,
        string deadlineAction)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.OperationExpired,
            null,
            new OperationExpiredBody(operationKey, policyVersion, source, deadlineAction),
            ProtocolJsonContext.Default.OperationExpiredBody);

    /// <summary>
    /// 创建操作进度汇报通知信封。
    /// </summary>
    public static IpcEnvelope CreateProgress(ProgressBody progress)
        => CreateBodyMessage(
            ProtocolConstants.MessageTypes.Progress,
            null,
            progress,
            ProtocolJsonContext.Default.ProgressBody);

    /// <summary>
    /// 按 Core 协议已知类型解析信封中的消息体。
    /// </summary>
    /// <typeparam name="T">消息体类型。</typeparam>
    /// <param name="envelope">待解析的协议信封。</param>
    /// <returns>反序列化后的消息体对象。</returns>
    /// <exception cref="ProtocolException">当缺少载荷或格式不合法时抛出。</exception>
    /// <exception cref="NotSupportedException">当 T 不是 Core 内置支持的消息体类型时抛出。</exception>
    public static T ReadBody<T>(IpcEnvelope envelope)
    {
        var typeInfo = ProtocolJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
            ?? throw new NotSupportedException($"类型 {typeof(T).FullName} 不是 Core 协议内置的已知消息体。");
        return ReadBody(envelope, typeInfo);
    }

    /// <summary>
    /// 使用调用方提供的源生成元数据解析信封中的消息体（支持泛型业务模型、DTO 或其他纯数据载荷）。
    /// </summary>
    /// <typeparam name="T">消息体类型。</typeparam>
    /// <param name="envelope">待解析的协议信封。</param>
    /// <param name="jsonTypeInfo">调用方提供的 JSON 类型元数据。</param>
    /// <returns>反序列化后的对象。</returns>
    /// <exception cref="ProtocolException">当缺少载荷或格式不合法时抛出。</exception>
    public static T ReadBody<T>(IpcEnvelope envelope, JsonTypeInfo<T> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        if (envelope.Payload is not JsonElement payload || payload.ValueKind == JsonValueKind.Undefined)
        {
            throw new ProtocolException($"消息 {envelope.Type} 缺少 payload。");
        }

        try
        {
            return JsonSerializer.Deserialize(payload, jsonTypeInfo)
                ?? throw new ProtocolException($"消息 {envelope.Type} 的 payload 为空。");
        }
        catch (JsonException exception)
        {
            throw new ProtocolException($"消息 {envelope.Type} 的 payload 格式无效。", exception);
        }
    }

    /// <summary>
    /// 校验补参答复体只包含一种合法形态：纯文本 value，或 payloadType 与结构化 payload 组合。
    /// </summary>
    /// <param name="value">补参答复体实例。</param>
    /// <exception cref="ProtocolException">当形态混用或缺失关键字段时抛出。</exception>
    public static void ValidateParamValueShape(ParamValueBody value)
        => EnvelopeValidator.ValidateParamValueShape(value, isReceiving: true);

    /// <summary>
    /// 把业务体序列化成 payload 并装进信封。
    /// </summary>
    internal static IpcEnvelope CreateBodyMessage<T>(
        string type,
        long? id,
        T body,
        JsonTypeInfo<T> jsonTypeInfo,
        string? traceParent = null,
        string? traceState = null)
        => new()
        {
            Type = type,
            Id = id,
            Payload = JsonSerializer.SerializeToElement(body, jsonTypeInfo),
            TraceParent = traceParent,
            TraceState = traceState
        };

    /// <summary>
    /// 将文本按 UTF-8 字节数安全截断并追加省略标记；保证输出不超过 maxBytes。
    /// 按 Unicode 码点枚举，防止截断半个字符。
    /// </summary>
    /// <param name="value">原始文本。</param>
    /// <param name="maxBytes">最大允许的 UTF-8 字节预算。</param>
    /// <returns>截断后的安全文本。</returns>
    public static string LimitUtf8(string value, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        const string suffix = "…[截断]";
        var suffixBytes = Encoding.UTF8.GetByteCount(suffix);

        if (maxBytes <= 0)
        {
            return string.Empty;
        }

        if (maxBytes < suffixBytes)
        {
            var marker = new StringBuilder();
            var markerBudget = maxBytes;
            foreach (var rune in suffix.EnumerateRunes())
            {
                if (markerBudget < rune.Utf8SequenceLength)
                {
                    break;
                }

                marker.Append(rune);
                markerBudget -= rune.Utf8SequenceLength;
            }

            return marker.ToString();
        }

        var budget = maxBytes - suffixBytes;
        var result = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (budget < rune.Utf8SequenceLength)
            {
                break;
            }

            result.Append(rune);
            budget -= rune.Utf8SequenceLength;
        }

        return result.Append(suffix).ToString();
    }
}
