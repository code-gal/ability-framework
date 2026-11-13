using System.Text.Json;

namespace Ability.Core;

/// <summary>
/// 集中执行 IPC 信封及特定协议消息体的结构合法性校验。
/// 统管本地发送前校验（结构违规抛出 ArgumentException / ArgumentNullException）与
/// 接收端帧校验（结构违规抛出 ProtocolException，标记连接已损坏）。
/// </summary>
internal static class EnvelopeValidator
{
    /// <summary>
    /// 校验信封基础结构以及核心业务消息体（TaskInput、SubmitResult、ResultItem、ParamValue、ChildMessage、ChildMessageReply）的字段完整性。
    /// 遇到子调用信封时递归核验内层信封结构。
    /// </summary>
    /// <param name="envelope">待校验的信封。</param>
    /// <param name="isReceiving">true 表示接收端反序列化后核验；false 表示发送端写出前核验。</param>
    internal static void ValidateEnvelopeStructure(IpcEnvelope envelope, bool isReceiving)
    {
        if (envelope is null)
        {
            if (isReceiving)
            {
                throw new ProtocolException("消息信封不能为空。");
            }

            throw new ArgumentNullException(nameof(envelope), "消息信封不能为空。");
        }

        if (string.IsNullOrWhiteSpace(envelope.Type))
        {
            Fail("消息类型不能为空。", nameof(envelope), isReceiving);
        }

        if (envelope.OptionalNotification && envelope.Id is not null)
        {
            Fail("可忽略扩展通知不能带请求 ID。", nameof(envelope), isReceiving);
        }

        switch (envelope.Type)
        {
            case ProtocolConstants.MessageTypes.TaskInput:
                ValidateTaskInput(envelope, isReceiving);
                break;

            case ProtocolConstants.MessageTypes.SubmitResult:
                ValidateSubmitResult(envelope, isReceiving);
                break;

            case ProtocolConstants.MessageTypes.ResultItem:
                ValidateResultItem(envelope, isReceiving);
                break;

            case ProtocolConstants.MessageTypes.ParamValue:
                ValidateParamValue(envelope, isReceiving);
                break;

            case ProtocolConstants.MessageTypes.ChildMessage:
                ValidateChildMessage(envelope, isReceiving);
                break;

            case ProtocolConstants.MessageTypes.ChildMessageReply:
                ValidateChildMessageReply(envelope, isReceiving);
                break;
        }
    }

    /// <summary>
    /// 校验首帧任务输入消息体的字段完整性。
    /// </summary>
    /// <param name="envelope">待校验的信封。</param>
    /// <param name="isReceiving">是否为接收端校验。</param>
    private static void ValidateTaskInput(IpcEnvelope envelope, bool isReceiving)
    {
        if (envelope.Payload is not JsonElement payloadElement || payloadElement.ValueKind != JsonValueKind.Object)
        {
            Fail("首帧任务输入缺少对象格式的 payload 载荷。", nameof(envelope), isReceiving);
            return;
        }

        TaskInputBody? body;
        try
        {
            body = JsonSerializer.Deserialize(payloadElement, ProtocolJsonContext.Default.TaskInputBody);
        }
        catch (JsonException ex)
        {
            Fail("首帧任务输入 payload 格式无效。", nameof(envelope), isReceiving, ex);
            return;
        }

        if (body is null)
        {
            Fail("首帧任务输入 payload 不能为空。", nameof(envelope), isReceiving);
            return;
        }

        if (string.IsNullOrWhiteSpace(body.TaskId))
        {
            Fail("首帧任务输入缺少必需的 taskId。", nameof(envelope), isReceiving);
        }

        if (string.IsNullOrWhiteSpace(body.TaskTempPath))
        {
            Fail("首帧任务输入缺少必需的 taskTempPath。", nameof(envelope), isReceiving);
        }

        if (string.IsNullOrWhiteSpace(body.PayloadType))
        {
            Fail("首帧任务输入缺少必需的 payloadType。", nameof(envelope), isReceiving);
        }

        if (body.Payload.ValueKind == JsonValueKind.Undefined)
        {
            Fail("首帧任务输入缺少必需的业务载荷 payload。", nameof(envelope), isReceiving);
        }
    }

    /// <summary>
    /// 校验一元执行结果消息体的字段完整性。
    /// </summary>
    /// <param name="envelope">待校验的信封。</param>
    /// <param name="isReceiving">是否为接收端校验。</param>
    private static void ValidateSubmitResult(IpcEnvelope envelope, bool isReceiving)
    {
        if (envelope.Payload is not JsonElement payloadElement || payloadElement.ValueKind != JsonValueKind.Object)
        {
            Fail("一元结果缺少对象格式的 payload 载荷。", nameof(envelope), isReceiving);
            return;
        }

        SubmitResultBody? body;
        try
        {
            body = JsonSerializer.Deserialize(payloadElement, ProtocolJsonContext.Default.SubmitResultBody);
        }
        catch (JsonException ex)
        {
            Fail("一元结果 payload 格式无效。", nameof(envelope), isReceiving, ex);
            return;
        }

        if (body is null)
        {
            Fail("一元结果 payload 不能为空。", nameof(envelope), isReceiving);
            return;
        }

        if (string.IsNullOrWhiteSpace(body.PayloadType))
        {
            Fail("一元结果缺少必需的 payloadType。", nameof(envelope), isReceiving);
        }

        if (body.Payload.ValueKind == JsonValueKind.Undefined)
        {
            Fail("一元结果缺少必需的业务载荷 payload。", nameof(envelope), isReceiving);
        }
    }

    /// <summary>
    /// 校验流式结果项消息体的字段完整性。
    /// </summary>
    /// <param name="envelope">待校验的信封。</param>
    /// <param name="isReceiving">是否为接收端校验。</param>
    private static void ValidateResultItem(IpcEnvelope envelope, bool isReceiving)
    {
        if (envelope.Payload is not JsonElement payloadElement || payloadElement.ValueKind != JsonValueKind.Object)
        {
            Fail("流元素结果缺少对象格式的 payload 载荷。", nameof(envelope), isReceiving);
            return;
        }

        ResultItemBody? body;
        try
        {
            body = JsonSerializer.Deserialize(payloadElement, ProtocolJsonContext.Default.ResultItemBody);
        }
        catch (JsonException ex)
        {
            Fail("流元素结果 payload 格式无效。", nameof(envelope), isReceiving, ex);
            return;
        }

        if (body is null)
        {
            Fail("流元素结果 payload 不能为空。", nameof(envelope), isReceiving);
            return;
        }

        if (body.Payload.ValueKind == JsonValueKind.Undefined)
        {
            Fail("流元素结果缺少必需的业务载荷 payload。", nameof(envelope), isReceiving);
        }
    }

    /// <summary>
    /// 校验补参答复消息体的完整性。
    /// </summary>
    /// <param name="envelope">待校验的信封。</param>
    /// <param name="isReceiving">是否为接收端校验。</param>
    private static void ValidateParamValue(IpcEnvelope envelope, bool isReceiving)
    {
        if (envelope.Payload is not JsonElement payloadElement || payloadElement.ValueKind != JsonValueKind.Object)
        {
            Fail("补参答复缺少对象格式的 payload 载荷。", nameof(envelope), isReceiving);
            return;
        }

        ParamValueBody? body;
        try
        {
            body = JsonSerializer.Deserialize(payloadElement, ProtocolJsonContext.Default.ParamValueBody);
        }
        catch (JsonException ex)
        {
            Fail("补参答复 payload 格式无效。", nameof(envelope), isReceiving, ex);
            return;
        }

        if (body is null)
        {
            Fail("补参答复 payload 不能为空。", nameof(envelope), isReceiving);
            return;
        }

        ValidateParamValueShape(body, isReceiving);
    }

    /// <summary>
    /// 校验补参答复体的互斥形状规则（纯文本与结构化载荷二选一）。
    /// </summary>
    /// <param name="value">补参答复体实例。</param>
    /// <param name="isReceiving">是否为接收端校验。</param>
    internal static void ValidateParamValueShape(ParamValueBody value, bool isReceiving)
    {
        if (value is null)
        {
            Fail("补参答复体不能为空。", nameof(value), isReceiving);
            return;
        }

        var hasValue = value.Value is not null;
        var hasPayloadType = !string.IsNullOrWhiteSpace(value.PayloadType);
        var hasPayload = value.Payload is not null && value.Payload.Value.ValueKind != JsonValueKind.Undefined;

        if (hasValue)
        {
            if (value.PayloadType is not null || value.Payload is not null)
            {
                Fail("补参答复必须只包含文本 value，或同时包含 payloadType 与 payload。", nameof(value), isReceiving);
            }

            return;
        }

        if (!hasPayloadType || !hasPayload)
        {
            Fail("补参答复必须只包含文本 value，或同时包含 payloadType 与 payload。", nameof(value), isReceiving);
        }
    }

    /// <summary>
    /// 校验子消息包装信封结构并递归验证内层信封。
    /// </summary>
    /// <param name="envelope">待校验的外层信封。</param>
    /// <param name="isReceiving">是否为接收端校验。</param>
    private static void ValidateChildMessage(IpcEnvelope envelope, bool isReceiving)
    {
        if (envelope.Payload is not JsonElement payloadElement || payloadElement.ValueKind != JsonValueKind.Object)
        {
            Fail("子消息缺少对象格式的 payload 载荷。", nameof(envelope), isReceiving);
            return;
        }

        ChildMessageBody? body;
        try
        {
            body = JsonSerializer.Deserialize(payloadElement, ProtocolJsonContext.Default.ChildMessageBody);
        }
        catch (JsonException ex)
        {
            Fail("子消息 payload 格式无效。", nameof(envelope), isReceiving, ex);
            return;
        }

        if (body is null)
        {
            Fail("子消息 payload 不能为空。", nameof(envelope), isReceiving);
            return;
        }

        if (string.IsNullOrWhiteSpace(body.InvocationKey))
        {
            Fail("子消息缺少 invocationKey。", nameof(envelope), isReceiving);
        }

        if (body.Message is null)
        {
            Fail("子消息缺少内层信封。", nameof(envelope), isReceiving);
            return;
        }

        // 递归核验内层信封结构
        ValidateEnvelopeStructure(body.Message, isReceiving);
    }

    /// <summary>
    /// 校验子消息答复包装信封结构并递归验证可选内层响应信封。
    /// </summary>
    /// <param name="envelope">待校验的外层信封。</param>
    /// <param name="isReceiving">是否为接收端校验。</param>
    private static void ValidateChildMessageReply(IpcEnvelope envelope, bool isReceiving)
    {
        if (envelope.Payload is not JsonElement payloadElement || payloadElement.ValueKind != JsonValueKind.Object)
        {
            Fail("子消息答复缺少对象格式的 payload 载荷。", nameof(envelope), isReceiving);
            return;
        }

        ChildMessageReplyBody? body;
        try
        {
            body = JsonSerializer.Deserialize(payloadElement, ProtocolJsonContext.Default.ChildMessageReplyBody);
        }
        catch (JsonException ex)
        {
            Fail("子消息答复 payload 格式无效。", nameof(envelope), isReceiving, ex);
            return;
        }

        if (body is null)
        {
            Fail("子消息答复 payload 不能为空。", nameof(envelope), isReceiving);
            return;
        }

        if (string.IsNullOrWhiteSpace(body.InvocationKey))
        {
            Fail("子消息答复缺少 invocationKey。", nameof(envelope), isReceiving);
        }

        if (body.Response is not null)
        {
            // 递归核验内层答复信封结构
            ValidateEnvelopeStructure(body.Response, isReceiving);
        }
    }

    /// <summary>
    /// 根据收发方向统一抛出分类异常：接收端损坏抛出 ProtocolException，发送端违规抛出 ArgumentException。
    /// </summary>
    /// <param name="message">错误信息。</param>
    /// <param name="paramName">参数名称。</param>
    /// <param name="isReceiving">是否为接收端。</param>
    /// <param name="inner">内部异常原因。</param>
    private static void Fail(string message, string paramName, bool isReceiving, Exception? inner = null)
    {
        if (isReceiving)
        {
            throw new ProtocolException(message, inner);
        }

        throw new ArgumentException(message, paramName, inner);
    }
}
