using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Ability.Core;

/// <summary>
/// 负责底层 IPC 帧的编解码、读写、计量与双层上限校验。
/// 传输格式为 4 字节小端长度前缀紧随严格 UTF-8 JSON 信封。
/// 单帧信封上限 16 MiB，内层业务载荷上限 1 MiB。
/// </summary>
public static partial class IpcFrame
{
    /// <summary>
    /// 完整 UTF-8 JSON 信封的最大允许字节数（16 MiB），不包含 4 字节长度前缀。
    /// </summary>
    public const int MaxEnvelopeBytes = ProtocolConstants.MaxEnvelopeBytes;

    /// <summary>
    /// 信封内业务 payload 的最大允许 UTF-8 字节数（1 MiB）。
    /// </summary>
    public const int MaxPayloadBytes = ProtocolConstants.MaxPayloadBytes;

    /// <summary>
    /// 业务编码最大工作空间上限（6 MiB + 16 字节）。
    /// </summary>
    public const int MaxEncodeWorkspaceBytes = ProtocolConstants.MaxEncodeWorkspaceBytes;

    /// <summary>
    /// 完整信封编码最大工作空间上限（128 MiB）。
    /// </summary>
    public const int MaxEnvelopeWorkspaceBytes = ProtocolConstants.MaxEnvelopeWorkspaceBytes;

    /// <summary>
    /// 测量完整信封的 UTF-8 JSON 序列化大小。
    /// 使用计数缓冲写出，不分配等大的完整内存缓冲。
    /// </summary>
    /// <param name="envelope">待测量的信封。</param>
    /// <returns>信封 UTF-8 JSON 字节数。</returns>
    public static long MeasureEnvelopeBytes(IpcEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return CountJson(writer => JsonSerializer.Serialize(
            writer,
            envelope,
            ProtocolJsonContext.Default.IpcEnvelope));
    }

    /// <summary>
    /// 对待发送信封进行结构与双层大小限制校验。
    /// 在有限工作空间内执行有界校验，超限及早抛出 <see cref="PayloadTooLargeException"/>，杜绝无界中间缓冲分配。
    /// </summary>
    /// <param name="envelope">待校验的信封。</param>
    /// <exception cref="ArgumentNullException">当信封为空时抛出（本地发送拒绝）。</exception>
    /// <exception cref="ArgumentException">当信封结构非法或内层信封缺失/非法时抛出（本地发送拒绝）。</exception>
    /// <exception cref="PayloadTooLargeException">当业务载荷或完整信封超限时抛出（本地发送拒绝）。</exception>
    public static void ValidateEnvelope(IpcEnvelope envelope)
    {
        EnvelopeValidator.ValidateEnvelopeStructure(envelope, isReceiving: false);
        ValidateBusinessPayloadBounded(envelope);
        _ = SerializeEnvelope(envelope);
    }

    /// <summary>
    /// 将信封校验并编码为完整的传输帧（4 字节小端长度前缀 + UTF-8 JSON 信封）。
    /// 在有限工作空间内执行单次有界序列化，超限及早抛出 <see cref="PayloadTooLargeException"/>。
    /// 返回的 byte 数组为独立分配的新缓冲区，所有权归调用方。
    /// </summary>
    /// <param name="envelope">待编码的信封。</param>
    /// <returns>包含长度前缀和信封正文的完整字节帧。</returns>
    /// <exception cref="PayloadTooLargeException">当载荷或信封超限时抛出。</exception>
    public static byte[] EncodeFrame(IpcEnvelope envelope)
    {
        EnvelopeValidator.ValidateEnvelopeStructure(envelope, isReceiving: false);
        ValidateBusinessPayloadBounded(envelope);
        var payload = SerializeEnvelope(envelope);

        var frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(sizeof(int)));
        return frame;
    }

    /// <summary>
    /// 从给定数据流中异步读取一个完整帧并反序列化为信封。
    /// 遵循以下所有权与责任规约：
    /// 1. 本方法不接管也不释放 <paramref name="stream"/> 的生命周期。
    /// 2. 在未读取到任何新帧字节（干净的 EOF）时返回 null。
    /// 3. 若在前缀或载荷读取中途遭遇 EOF，视为连接损坏抛出 <see cref="ProtocolException"/>。
    /// 4. 严格校验长度范围（0..16 MiB）、UTF-8 JSON 语法、无 ID 通知约束及 1 MiB 业务载荷限制。
    /// </summary>
    /// <param name="stream">双工数据流。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解析出的信封；若正常到达 EOF 则返回 null。</returns>
    /// <exception cref="ProtocolException">当数据损坏、截断、超限或违反协议规约时抛出。</exception>
    public static async ValueTask<IpcEnvelope?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var lengthBuffer = new byte[sizeof(int)];
        var firstRead = await stream.ReadAsync(lengthBuffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (firstRead == 0) return null;

        try
        {
            await stream.ReadExactlyAsync(lengthBuffer.AsMemory(firstRead), cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw new ProtocolException("消息长度前缀未完整读取，连接已断开。", exception);
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (payloadLength < 0 || payloadLength > MaxEnvelopeBytes)
        {
            throw new ProtocolException($"消息载荷长度 {payloadLength} 超过允许范围 0..{MaxEnvelopeBytes} 字节。");
        }

        var payloadBuffer = new byte[payloadLength];
        try
        {
            await stream.ReadExactlyAsync(payloadBuffer, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw new ProtocolException("消息载荷未完整读取，连接已断开。", exception);
        }

        try
        {
            var envelope = JsonSerializer.Deserialize(payloadBuffer, ProtocolJsonContext.Default.IpcEnvelope)
                ?? throw new ProtocolException("消息 JSON 为空。");

            // 集中结构合法性校验（递归检查类型名、无 ID 通知、子消息内层、TaskInput/SubmitResult/ResultItem 消息体与载荷等，不合格抛出 ProtocolException 视为连接损坏）
            EnvelopeValidator.ValidateEnvelopeStructure(envelope, isReceiving: true);
            try
            {
                ValidateBusinessPayloadBounded(envelope, isReceiving: true);
            }
            catch (PayloadTooLargeException ex)
            {
                throw new ProtocolException(ex.Message, ex);
            }

            return envelope;
        }
        catch (JsonException exception)
        {
            throw new ProtocolException("消息不是有效的 UTF-8 JSON 信封。", exception);
        }
        catch (ArgumentException exception)
        {
            throw new ProtocolException("消息结构无效或违反协议约束。", exception);
        }
    }

    /// <summary>
    /// 将信封编码为完整帧并异步写入数据流，随后立即刷新（Flush）。
    /// 不接管或关闭流。
    /// </summary>
    /// <param name="stream">目标写入流。</param>
    /// <param name="envelope">待写出的信封。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async ValueTask WriteAsync(
        Stream stream,
        IpcEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var frame = EncodeFrame(envelope);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 测量给定信封内的业务 payload 字节数；内嵌信封按内层类型递归判定。
    /// 仅非业务载荷消息或协议允许缺省的字段计为 0。
    /// 缺少必需消息体、格式解析失败或载荷为 Undefined 时明确报错。
    /// </summary>
    /// <param name="envelope">待判定的信封。</param>
    /// <returns>业务载荷的 UTF-8 JSON 字节数。</returns>
    /// <exception cref="ArgumentNullException">当信封为空时抛出。</exception>
    /// <exception cref="ArgumentException">当必需消息体缺失、格式解析失败或载荷为 Undefined 时抛出。</exception>
    public static long MeasureBusinessPayloadBytes(IpcEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return envelope.Type switch
        {
            ProtocolConstants.MessageTypes.TaskInput => MeasureTaskInput(envelope),
            ProtocolConstants.MessageTypes.SubmitResult => MeasureSubmitResult(envelope),
            ProtocolConstants.MessageTypes.ResultItem => MeasureResultItem(envelope),
            ProtocolConstants.MessageTypes.ParamValue => MeasureParamValue(envelope),
            ProtocolConstants.MessageTypes.ChildMessage => MeasureChildMessage(envelope),
            ProtocolConstants.MessageTypes.ChildMessageReply => MeasureChildMessageReply(envelope),
            _ => 0
        };
    }

    /// <summary>
    /// 测量单个业务 payload JSON 元素的 UTF-8 字节数。
    /// 合法 JSON（包括 null）按实际序列化字节数计量；Undefined 抛出 <see cref="ArgumentException"/>。
    /// </summary>
    /// <param name="payload">待测量的 JSON 元素。</param>
    /// <returns>UTF-8 字节数。</returns>
    /// <exception cref="ArgumentException">当元素为 Undefined 时抛出。</exception>
    public static long MeasurePayloadBytes(JsonElement payload)
        => MeasureElement(payload);

    /// <summary>
    /// 测量文本业务载荷编码为 JSON 字符串后的 UTF-8 字节数（包含两端双引号与内部转义字符开销）。
    /// </summary>
    /// <param name="value">待测量的文本内容。</param>
    /// <returns>JSON 字符串的实际 UTF-8 字节数。</returns>
    public static long MeasureStringPayloadBytes(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return CountJson(writer => writer.WriteStringValue(value));
    }

    /// <summary>
    /// 使用指定的源生成元数据测量泛型业务载荷（模型或 DTO）的 UTF-8 JSON 序列化字节数。
    /// 与实际发送使用相同的序列化配置。
    /// </summary>
    public static long MeasureModelPayloadBytes<TModel>(
        TModel model,
        JsonTypeInfo<TModel> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        return JsonSerializer.SerializeToUtf8Bytes(model, jsonTypeInfo).LongLength;
    }

    /// <summary>
    /// 预先测量 SubmitResult 信封总大小（业务载荷数据字节数 + 结构常量开销）。
    /// </summary>
    public static long MeasureSubmitResultEnvelope<TModel>(
        string payloadType,
        TModel model,
        JsonTypeInfo<TModel> jsonTypeInfo)
        => MeasureModelPayloadBytes(model, jsonTypeInfo)
            + SubmitResultHead(payloadType).Sum(part => (long)part.Length)
            + EnvelopeSuffix.Length;

    /// <summary>
    /// 预先测量 ResultItem 信封总大小（流结果项数据字节数 + 结构常量开销）。
    /// </summary>
    public static long MeasureResultItemEnvelope<TModel>(
        TModel model,
        JsonTypeInfo<TModel> jsonTypeInfo)
        => MeasureModelPayloadBytes(model, jsonTypeInfo)
            + ResultItemHead().Sum(part => (long)part.Length)
            + EnvelopeSuffix.Length;

    /// <summary>
    /// 预先测量 TaskInput 信封总大小；供宿主或父调用在启动子进程前核验输入尺寸。
    /// </summary>
    public static long MeasureTaskInputEnvelope<TInput>(
        string taskId,
        string taskTempPath,
        string payloadType,
        TInput input,
        JsonTypeInfo<TInput> jsonTypeInfo)
        => MeasureModelPayloadBytes(input, jsonTypeInfo)
            + TaskInputHead(taskId, taskTempPath, payloadType).Sum(part => (long)part.Length)
            + EnvelopeSuffix.Length;

    /// <summary>
    /// 高效编码一元结果帧。
    /// 业务载荷数据（模型或 DTO）在有界缓冲中直接序列化，超过 1 MiB 立即失败；
    /// 随后直接拼装协议头与尾，避免二次全量反序列化与中间 JsonElement 分配。
    /// </summary>
    public static byte[] EncodeSubmitResultFrame<TModel>(
        string payloadType,
        TModel model,
        JsonTypeInfo<TModel> jsonTypeInfo)
    {
        // 快捷编码绕过信封对象，仍须在序列化业务载荷前执行同样的类型名检查。
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadType);
        return BuildBusinessFrame(ProtocolConstants.MessageTypes.SubmitResult, SubmitResultHead(payloadType), model, jsonTypeInfo);
    }

    /// <summary>
    /// 高效编码流元素帧。
    /// </summary>
    public static byte[] EncodeResultItemFrame<TModel>(
        TModel model,
        JsonTypeInfo<TModel> jsonTypeInfo)
        => BuildBusinessFrame(ProtocolConstants.MessageTypes.ResultItem, ResultItemHead(), model, jsonTypeInfo);

    /// <summary>
    /// 按双层上限校验已计数的业务帧大小。
    /// </summary>
    /// <param name="messageType">消息类型标识。</param>
    /// <param name="payloadBytes">业务载荷大小。</param>
    /// <param name="envelopeBytes">完整信封大小。</param>
    /// <exception cref="PayloadTooLargeException">若任一项超限时抛出。</exception>
    public static void ValidateFrameSizes(string messageType, long payloadBytes, long envelopeBytes)
    {
        if (payloadBytes > MaxPayloadBytes)
        {
            throw new PayloadTooLargeException(
                $"消息 {messageType} 的业务载荷 {payloadBytes} 字节超过上限 {MaxPayloadBytes} 字节。");
        }

        if (envelopeBytes > MaxEnvelopeBytes)
        {
            throw new PayloadTooLargeException(
                $"消息 {messageType} 的完整信封 UTF-8 大小 {envelopeBytes} 字节超过上限 {MaxEnvelopeBytes} 字节。");
        }
    }

    /// <summary>
    /// 组装业务消息完整帧：先将业务载荷数据序列化入载荷有界缓冲，再拼装小端前缀、固定头部、业务载荷与后缀。
    /// </summary>
    /// <typeparam name="TModel">业务载荷类型。</typeparam>
    /// <param name="messageType">消息类型名称。</param>
    /// <param name="head">帧头部静态字节块序列。</param>
    /// <param name="model">业务载荷数据实例。</param>
    /// <param name="jsonTypeInfo">业务载荷的 JSON 序列化元数据。</param>
    /// <returns>编码完成的完整帧字节数组。</returns>
    private static byte[] BuildBusinessFrame<TModel>(
        string messageType,
        IReadOnlyList<byte[]> head,
        TModel model,
        JsonTypeInfo<TModel> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        var payload = SerializeWithinPayloadLimit(model, jsonTypeInfo, messageType);
        var envelopeBytes = payload.LongLength
            + head.Sum(part => (long)part.Length)
            + EnvelopeSuffix.Length;

        if (envelopeBytes > MaxEnvelopeBytes)
        {
            throw new PayloadTooLargeException(
                $"消息 {messageType} 的完整信封 UTF-8 大小 {envelopeBytes} 字节超过上限 {MaxEnvelopeBytes} 字节。");
        }

        var frame = new byte[sizeof(int) + envelopeBytes];
        BinaryPrimitives.WriteInt32LittleEndian(frame, (int)envelopeBytes);
        var offset = sizeof(int);
        foreach (var part in head)
        {
            part.CopyTo(frame.AsSpan(offset));
            offset += part.Length;
        }

        payload.CopyTo(frame.AsSpan(offset));
        offset += payload.Length;
        EnvelopeSuffix.CopyTo(frame.AsSpan(offset));
        ValidateCompleteEnvelopeDepth(frame.AsSpan(sizeof(int), (int)envelopeBytes));
        return frame;
    }

    /// <summary>
    /// 在业务载荷上限（1 MiB）限制内序列化业务模型。
    /// </summary>
    /// <typeparam name="TModel">业务载荷类型。</typeparam>
    /// <param name="model">业务载荷模型实例。</param>
    /// <param name="jsonTypeInfo">业务模型的 JSON 序列化元数据。</param>
    /// <param name="messageType">消息类型名称。</param>
    /// <returns>序列化完成的 UTF-8 字节数组。</returns>
    private static byte[] SerializeWithinPayloadLimit<TModel>(
        TModel model,
        JsonTypeInfo<TModel> jsonTypeInfo,
        string messageType)
    {
        var buffer = new BoundedBufferWriter(MaxPayloadBytes, ProtocolConstants.MaxEncodeWorkspaceBytes);
        try
        {
            using var writer = new Utf8JsonWriter(buffer, CreateWriterOptions(jsonTypeInfo));
            JsonSerializer.Serialize(writer, model, jsonTypeInfo);
        }
        catch (PayloadTooLargeException exception)
        {
            throw new PayloadTooLargeException(
                $"消息 {messageType} 的业务载荷超过上限 {MaxPayloadBytes} 字节：{exception.Message}");
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// 从调用方提供的 JsonTypeInfo 创建对应的 JsonWriterOptions 选项。
    /// </summary>
    /// <typeparam name="TModel">业务载荷类型。</typeparam>
    /// <param name="jsonTypeInfo">业务模型的 JSON 序列化元数据。</param>
    /// <returns>配置好的 JsonWriterOptions。</returns>
    private static JsonWriterOptions CreateWriterOptions<TModel>(JsonTypeInfo<TModel> jsonTypeInfo)
    {
        var options = jsonTypeInfo.Options;
        return new JsonWriterOptions
        {
            Encoder = options.Encoder,
            Indented = options.WriteIndented,
            IndentCharacter = options.IndentCharacter,
            IndentSize = options.IndentSize,
            MaxDepth = options.MaxDepth,
            NewLine = options.NewLine
        };
    }

    /// <summary>
    /// 构造 SubmitResult 消息帧的静态头部字节分片。
    /// </summary>
    /// <param name="payloadType">载荷类型名称。</param>
    /// <returns>头部字节分片数组。</returns>
    private static byte[][] SubmitResultHead(string payloadType)
        =>
        [
            Encoding.UTF8.GetBytes("{\"type\":\"SubmitResult\",\"payload\":{\"payloadType\":"),
            EscapeJsonString(payloadType),
            Encoding.UTF8.GetBytes(",\"payload\":")
        ];

    /// <summary>
    /// 构造 ResultItem 消息帧的静态头部字节分片。
    /// </summary>
    /// <returns>头部字节分片数组。</returns>
    private static byte[][] ResultItemHead()
        =>
        [
            Encoding.UTF8.GetBytes("{\"type\":\"ResultItem\",\"payload\":{\"payload\":")
        ];

    /// <summary>
    /// 构造 TaskInput 消息帧的静态头部字节分片。
    /// </summary>
    /// <param name="taskId">任务 ID。</param>
    /// <param name="taskTempPath">任务临时目录路径。</param>
    /// <param name="payloadType">载荷类型名称。</param>
    /// <returns>头部字节分片数组。</returns>
    private static byte[][] TaskInputHead(string taskId, string taskTempPath, string payloadType)
        =>
        [
            Encoding.UTF8.GetBytes("{\"type\":\"TaskInput\",\"payload\":{\"taskId\":"),
            EscapeJsonString(taskId),
            Encoding.UTF8.GetBytes(",\"taskTempPath\":"),
            EscapeJsonString(taskTempPath),
            Encoding.UTF8.GetBytes(",\"payloadType\":"),
            EscapeJsonString(payloadType),
            Encoding.UTF8.GetBytes(",\"payload\":")
        ];

    /// <summary>
    /// 将字符串序列化为符合 JSON 转义规则的 UTF-8 字节数组。
    /// </summary>
    /// <param name="value">待转义的字符串。</param>
    /// <returns>转义后的 UTF-8 字节数组。</returns>
    private static byte[] EscapeJsonString(string value)
        => JsonSerializer.SerializeToUtf8Bytes(value, ProtocolJsonContext.Default.String);

    /// <summary>
    /// 通用信封序列化、公开计量与通用载荷重编码检查统一使用的 JSON 写入器配置。
    /// 遵循默认转义规则，确保通用信封写出、公开计量与载荷核验的字节数严格一致。
    /// </summary>
    private static readonly JsonWriterOptions GeneralWriterOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
    };

    /// <summary>
    /// 将信封序列化为 UTF-8 字节数组。
    /// 使用有界缓冲写出器限制工作空间上限，超限时及早抛出 PayloadTooLargeException。
    /// </summary>
    /// <param name="envelope">待序列化的信封。</param>
    /// <returns>序列化完成的 UTF-8 字节数组。</returns>
    private static byte[] SerializeEnvelope(IpcEnvelope envelope)
    {
        var writer = new BoundedBufferWriter(MaxEnvelopeBytes, MaxEnvelopeWorkspaceBytes);
        using (var jsonWriter = new Utf8JsonWriter(writer, GeneralWriterOptions))
        {
            JsonSerializer.Serialize(jsonWriter, envelope, ProtocolJsonContext.Default.IpcEnvelope);
        }
        var payload = writer.ToArray();
        ValidateCompleteEnvelopeDepth(payload);
        return payload;
    }

    /// <summary>
    /// 在有界工作空间内校验信封的业务载荷大小，超限时抛出 <see cref="PayloadTooLargeException"/>。
    /// 杜绝在发送前或读取后分配无界中间缓冲区。
    /// </summary>
    /// <param name="envelope">待校验的信封。</param>
    /// <param name="isReceiving">是否为入站接收校验（true 时依据线协议切片物理大小，false 时按通用写出配置核验）。</param>
    public static void ValidateBusinessPayloadBounded(IpcEnvelope envelope, bool isReceiving = false)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        switch (envelope.Type)
        {
            case ProtocolConstants.MessageTypes.TaskInput:
            {
                var body = RequireBody(envelope, ProtocolJsonContext.Default.TaskInputBody);
                ValidateJsonElementBounded(body.Payload, envelope.Type, isReceiving);
                break;
            }
            case ProtocolConstants.MessageTypes.SubmitResult:
            {
                var body = RequireBody(envelope, ProtocolJsonContext.Default.SubmitResultBody);
                ValidateJsonElementBounded(body.Payload, envelope.Type, isReceiving);
                break;
            }
            case ProtocolConstants.MessageTypes.ResultItem:
            {
                var body = RequireBody(envelope, ProtocolJsonContext.Default.ResultItemBody);
                ValidateJsonElementBounded(body.Payload, envelope.Type, isReceiving);
                break;
            }
            case ProtocolConstants.MessageTypes.ParamValue:
            {
                var body = RequireBody(envelope, ProtocolJsonContext.Default.ParamValueBody);
                if (body.Value is not null)
                {
                    if (isReceiving && TryGetRawValueToken(envelope, out var rawToken))
                    {
                        if (rawToken.Length > MaxPayloadBytes)
                        {
                            throw new PayloadTooLargeException(
                                $"消息 {envelope.Type} 的文本业务载荷线协议大小 {rawToken.Length} 字节超过上限 {MaxPayloadBytes} 字节。");
                        }
                    }
                    else
                    {
                        ValidateStringPayloadBounded(body.Value, envelope.Type);
                    }
                }
                else if (body.Payload is JsonElement payload)
                {
                    ValidateJsonElementBounded(payload, envelope.Type, isReceiving);
                }
                break;
            }
            case ProtocolConstants.MessageTypes.ChildMessage:
            {
                var child = RequireBody(envelope, ProtocolJsonContext.Default.ChildMessageBody);
                if (child.Message is not null)
                {
                    ValidateBusinessPayloadBounded(child.Message, isReceiving);
                }
                break;
            }
            case ProtocolConstants.MessageTypes.ChildMessageReply:
            {
                var reply = RequireBody(envelope, ProtocolJsonContext.Default.ChildMessageReplyBody);
                if (reply.Response is not null)
                {
                    ValidateBusinessPayloadBounded(reply.Response, isReceiving);
                }
                break;
            }
        }
    }

    /// <summary>
    /// 在有界工作空间内校验 JsonElement 业务载荷，超限抛出 <see cref="PayloadTooLargeException"/>。
    /// 采用默认单载荷与编码工作空间上限。
    /// </summary>
    /// <param name="element">待校验的 JSON 元素。</param>
    /// <param name="messageType">消息类型名称。</param>
    /// <param name="isReceiving">是否为入站接收阶段。</param>
    public static void ValidateJsonElementBounded(JsonElement element, string messageType, bool isReceiving = false)
        => ValidateJsonElementBounded(element, MaxPayloadBytes, MaxEncodeWorkspaceBytes, messageType, isReceiving);

    /// <summary>
    /// 在指定上限和工作空间内校验 JsonElement 业务载荷，超限抛出 <see cref="PayloadTooLargeException"/>。
    /// 在进入序列化前执行低成本前置 Token 检查，拦截超大 Token 避免进入 Utf8JsonWriter 分配大缓冲。
    /// 统一基于线协议物理字节规则核验：若元素已关联原始线协议切片且未超限，基于原始切片完成 Token 校验；
    /// 若无原始切片或为新构造载荷，统一按通用信封写出器配置（默认编码器）重编码核验，
    /// 确保实际写出、公开计量与入站核验对齐，杜绝宽松绕过与合法误拒。
    /// </summary>
    /// <param name="element">待校验的 JSON 元素。</param>
    /// <param name="maxPayloadBytes">业务载荷上限字节数。</param>
    /// <param name="maxWorkspaceBytes">编码工作空间上限字节数。</param>
    /// <param name="messageType">消息类型名称。</param>
    /// <param name="isReceiving">是否为入站接收阶段。</param>
    public static void ValidateJsonElementBounded(
        JsonElement element,
        int maxPayloadBytes,
        int maxWorkspaceBytes,
        string messageType = "BusinessPayload",
        bool isReceiving = false)
    {
        if (element.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException($"消息 {messageType} 的业务载荷未定义 (Undefined)。");
        }

        if (isReceiving)
        {
            var rawUtf8 = JsonMarshal.GetRawUtf8Value(element);
            if (!rawUtf8.IsEmpty)
            {
                if (rawUtf8.Length > maxPayloadBytes)
                {
                    throw new PayloadTooLargeException(
                        $"消息 {messageType} 的业务载荷线协议大小 {rawUtf8.Length} 字节超过上限 {maxPayloadBytes} 字节。");
                }

                PreCheckJsonTokens(element, maxPayloadBytes);
                return;
            }
        }

        PreCheckJsonTokens(element, maxPayloadBytes);

        var writer = new BoundedBufferWriter(maxPayloadBytes, maxWorkspaceBytes);
        try
        {
            using var jsonWriter = new Utf8JsonWriter(writer, GeneralWriterOptions);
            element.WriteTo(jsonWriter);
        }
        catch (PayloadTooLargeException)
        {
            throw new PayloadTooLargeException(
                $"消息 {messageType} 的业务载荷超过上限 {maxPayloadBytes} 字节。");
        }
    }

    /// <summary>
    /// 对已解析的 JSON 元素结构执行低成本前置 Token 迭代检查，及早拦截超限 Token 与超深嵌套，避免进入 Utf8JsonWriter 分配大缓冲。
    /// 基于 <see cref="Utf8JsonReader"/> 以迭代方式遍历所有 Token，避免创建与 Token 长度等比的中间字符串，扫描状态受深度上限（1000）约束且避免调用栈随输入深度增长。
    /// 允许尾逗号并跳过注释以兼容合法已解析值的多种原始字节表现（线协议入站解析规则保持现状）。
    /// 属性名与字符串值共用反转义长度判定逻辑。
    /// </summary>
    /// <param name="element">待检查的 JSON 元素。</param>
    /// <param name="maxPayloadBytes">业务载荷上限字节数。</param>
    private static void PreCheckJsonTokens(JsonElement element, int maxPayloadBytes)
    {
        var rawUtf8 = JsonMarshal.GetRawUtf8Value(element);
        if (rawUtf8.IsEmpty)
        {
            return;
        }

        var readerOptions = new JsonReaderOptions
        {
            MaxDepth = 1000,
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        };
        var reader = new Utf8JsonReader(rawUtf8, readerOptions);
        while (reader.Read())
        {
            if (reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String)
            {
                CheckTokenContentLength(
                    reader.ValueSpan,
                    reader.ValueIsEscaped,
                    maxPayloadBytes,
                    reader.TokenType == JsonTokenType.PropertyName ? "JSON 属性名" : "JSON 字符串");
            }
        }
    }

    /// <summary>
    /// 核验单个 Token（属性名或字符串）内容在反转义后的实际 UTF-8 字节数是否超过载荷上限，避免创建与 Token 长度等比的中间字符串。
    /// </summary>
    /// <param name="content">无外层双引号的原始 Token 字节切片。</param>
    /// <param name="isEscaped">当前 Token 是否包含转义字符。</param>
    /// <param name="maxPayloadBytes">业务载荷上限字节数。</param>
    /// <param name="tokenKind">Token 种类描述（如 "JSON 属性名" 或 "JSON 字符串"）。</param>
    private static void CheckTokenContentLength(
        ReadOnlySpan<byte> content,
        bool isEscaped,
        int maxPayloadBytes,
        string tokenKind)
    {
        if (!isEscaped)
        {
            if (content.Length > maxPayloadBytes)
            {
                throw new PayloadTooLargeException(
                    $"{tokenKind} Token 长度 {content.Length} 超过业务载荷上限 {maxPayloadBytes} 字节。");
            }
            return;
        }

        if (content.Length <= maxPayloadBytes) return;

        if (content.Length > (long)maxPayloadBytes * 6)
        {
            throw new PayloadTooLargeException($"{tokenKind} Token 长度超过业务载荷上限 {maxPayloadBytes} 字节。");
        }

        var unescapedBytes = GetUnescapedContentByteCount(content);
        if (unescapedBytes > maxPayloadBytes)
        {
            throw new PayloadTooLargeException(
                $"{tokenKind} Token 长度 {unescapedBytes} 超过业务载荷上限 {maxPayloadBytes} 字节。");
        }
    }

    /// <summary>
    /// 计算无双引号的原始 UTF-8 字节切片在反转义后的实际 UTF-8 字节数，避免创建中间字符串。
    /// </summary>
    /// <param name="content">无双引号的原始 UTF-8 字节切片。</param>
    /// <returns>反转义后的实际 UTF-8 字节计数。</returns>
    private static int GetUnescapedContentByteCount(ReadOnlySpan<byte> content)
    {
        var slashIdx = content.IndexOf((byte)'\\');
        if (slashIdx < 0) return content.Length;

        var count = 0;
        var i = 0;
        while (i < content.Length)
        {
            if (content[i] == (byte)'\\')
            {
                i++;
                if (i >= content.Length) break;

                var c = content[i++];
                if (c == (byte)'u')
                {
                    if (i + 4 <= content.Length && TryParseHex4(content.Slice(i, 4), out var codePoint))
                    {
                        i += 4;
                        if (char.IsHighSurrogate((char)codePoint) &&
                            i + 6 <= content.Length &&
                            content[i] == (byte)'\\' && content[i + 1] == (byte)'u' &&
                            TryParseHex4(content.Slice(i + 2, 4), out var lowPoint) &&
                            char.IsLowSurrogate((char)lowPoint))
                        {
                            i += 6;
                            count += 4;
                        }
                        else
                        {
                            count += codePoint switch { <= 0x7F => 1, <= 0x7FF => 2, _ => 3 };
                        }
                    }
                    else { count++; }
                }
                else { count++; }
            }
            else
            {
                count++;
                i++;
            }
        }

        return count;
    }

    /// <summary>
    /// 解析 4 字节十六进制 UTF-8 切片为整数码点。
    /// </summary>
    /// <param name="hex">4 字节十六进制切片。</param>
    /// <param name="value">解析成功的整数值。</param>
    /// <returns>若成功解析返回 true，否则 false。</returns>
    private static bool TryParseHex4(ReadOnlySpan<byte> hex, out int value)
    {
        value = 0;
        for (var i = 0; i < 4; i++)
        {
            var b = hex[i];
            var digit = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
                >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
                _ => -1,
            };
            if (digit < 0) return false;
            value = (value << 4) | digit;
        }
        return true;
    }

    /// <summary>
    /// 在有界工作空间内校验字符串业务载荷，超限抛出 <see cref="PayloadTooLargeException"/>。
    /// 采用默认单载荷与编码工作空间上限。
    /// </summary>
    /// <param name="value">待校验的文本值。</param>
    /// <param name="messageType">消息类型名称。</param>
    public static void ValidateStringPayloadBounded(string value, string messageType)
        => ValidateStringPayloadBounded(value, MaxPayloadBytes, MaxEncodeWorkspaceBytes, messageType);

    /// <summary>
    /// 在指定上限和工作空间内校验字符串业务载荷，超限抛出 <see cref="PayloadTooLargeException"/>。
    /// 先行执行快速长度与 UTF-8 字节计数校验，避免进入 Utf8JsonWriter。
    /// </summary>
    /// <param name="value">待校验的文本值。</param>
    /// <param name="maxPayloadBytes">业务载荷上限字节数。</param>
    /// <param name="maxWorkspaceBytes">编码工作空间上限字节数。</param>
    /// <param name="messageType">消息类型名称。</param>
    public static void ValidateStringPayloadBounded(
        string value,
        int maxPayloadBytes,
        int maxWorkspaceBytes,
        string messageType = "BusinessPayload")
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length > maxPayloadBytes || Encoding.UTF8.GetByteCount(value) > maxPayloadBytes)
        {
            throw new PayloadTooLargeException(
                $"消息 {messageType} 的业务载荷超过上限 {maxPayloadBytes} 字节。");
        }

        var writer = new BoundedBufferWriter(maxPayloadBytes, maxWorkspaceBytes);
        try
        {
            using var jsonWriter = new Utf8JsonWriter(writer, GeneralWriterOptions);
            jsonWriter.WriteStringValue(value);
        }
        catch (PayloadTooLargeException)
        {
            throw new PayloadTooLargeException(
                $"消息 {messageType} 的业务载荷超过上限 {maxPayloadBytes} 字节。");
        }
    }

    /// <summary>
    /// 从 ParamValue 信封的原始 JSON 载荷中提取最终生效的 value 字段对应的 Token 原始字节切片（包含双引号与转义）。
    /// 遵循反序列化规则：允许重复属性与大小写变体，取最后一个匹配的字符串属性作为最终生效值。
    /// </summary>
    /// <param name="envelope">信封实例。</param>
    /// <param name="rawToken">提取成功的原始 Token 字节切片。</param>
    /// <returns>若成功提取到有效的原始切片返回 true，否则返回 false。</returns>
    private static bool TryGetRawValueToken(IpcEnvelope envelope, out ReadOnlySpan<byte> rawToken)
    {
        rawToken = default;
        if (envelope.Payload is not JsonElement payload || payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var found = false;
        foreach (var prop in payload.EnumerateObject())
        {
            if (string.Equals(prop.Name, "value", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
            {
                var candidate = JsonMarshal.GetRawUtf8Value(prop.Value);
                if (!candidate.IsEmpty)
                {
                    rawToken = candidate;
                    found = true;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// 业务帧信封闭合后缀字节数组。
    /// </summary>
    private static readonly byte[] EnvelopeSuffix = Encoding.UTF8.GetBytes("}}");

    /// <summary>
    /// 计量 TaskInput 消息体中的业务载荷大小。
    /// </summary>
    /// <param name="envelope">待计量的信封。</param>
    /// <returns>业务载荷 UTF-8 字节数。</returns>
    private static long MeasureTaskInput(IpcEnvelope envelope)
    {
        var body = RequireBody(envelope, ProtocolJsonContext.Default.TaskInputBody);
        return body.Payload.ValueKind == JsonValueKind.Undefined
            ? throw new ArgumentException("TaskInput 消息缺少必需的业务载荷 payload。", nameof(envelope))
            : MeasureElement(body.Payload);
    }

    /// <summary>
    /// 计量 SubmitResult 消息体中的业务载荷大小。
    /// </summary>
    /// <param name="envelope">待计量的信封。</param>
    /// <returns>业务载荷 UTF-8 字节数。</returns>
    private static long MeasureSubmitResult(IpcEnvelope envelope)
    {
        var body = RequireBody(envelope, ProtocolJsonContext.Default.SubmitResultBody);
        return body.Payload.ValueKind == JsonValueKind.Undefined
            ? throw new ArgumentException("SubmitResult 消息缺少必需的业务载荷 payload。", nameof(envelope))
            : MeasureElement(body.Payload);
    }

    /// <summary>
    /// 计量 ResultItem 消息体中的业务载荷大小。
    /// </summary>
    /// <param name="envelope">待计量的信封。</param>
    /// <returns>业务载荷 UTF-8 字节数。</returns>
    private static long MeasureResultItem(IpcEnvelope envelope)
    {
        var body = RequireBody(envelope, ProtocolJsonContext.Default.ResultItemBody);
        return body.Payload.ValueKind == JsonValueKind.Undefined
            ? throw new ArgumentException("ResultItem 消息缺少必需的业务载荷 payload。", nameof(envelope))
            : MeasureElement(body.Payload);
    }

    /// <summary>
    /// 计量 ChildMessage 消息体内层信封的业务载荷大小。
    /// </summary>
    /// <param name="envelope">待计量的信封。</param>
    /// <returns>业务载荷 UTF-8 字节数。</returns>
    private static long MeasureChildMessage(IpcEnvelope envelope)
    {
        var body = RequireBody(envelope, ProtocolJsonContext.Default.ChildMessageBody);
        if (body.Message is null)
        {
            throw new ArgumentException("ChildMessage 消息缺少内层信封。", nameof(envelope));
        }

        return MeasureBusinessPayloadBytes(body.Message);
    }

    /// <summary>
    /// 计量 ChildMessageReply 消息体内层响应信封的业务载荷大小。
    /// </summary>
    /// <param name="envelope">待计量的信封。</param>
    /// <returns>业务载荷 UTF-8 字节数。</returns>
    private static long MeasureChildMessageReply(IpcEnvelope envelope)
    {
        var body = RequireBody(envelope, ProtocolJsonContext.Default.ChildMessageReplyBody);
        return body.Response is null ? 0 : MeasureBusinessPayloadBytes(body.Response);
    }

    /// <summary>
    /// 计量 ParamValue 消息体的有效载荷大小。
    /// </summary>
    /// <param name="envelope">待计量的信封。</param>
    /// <returns>业务载荷 UTF-8 字节数。</returns>
    private static long MeasureParamValue(IpcEnvelope envelope)
    {
        var body = RequireBody(envelope, ProtocolJsonContext.Default.ParamValueBody);
        EnvelopeValidator.ValidateParamValueShape(body, isReceiving: false);

        var stringBytes = body.Value is null
            ? 0
            : CountJson(writer => writer.WriteStringValue(body.Value));
        var payloadBytes = body.Payload is JsonElement payload ? MeasureElement(payload) : 0;
        return Math.Max(stringBytes, payloadBytes);
    }

    /// <summary>
    /// 计量指定 JsonElement 序列化后的 UTF-8 字节数。
    /// </summary>
    /// <param name="element">待测量的 JSON 元素。</param>
    /// <returns>UTF-8 字节数。</returns>
    private static long MeasureElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("待测量的 JSON 元素未定义 (Undefined)。", nameof(element));
        }

        return CountJson(writer => element.WriteTo(writer));
    }

    /// <summary>
    /// 通过计数字节写入器统计写入的 JSON UTF-8 字节数。
    /// </summary>
    /// <param name="write">写入委托动作。</param>
    /// <returns>写入的 UTF-8 字节数。</returns>
    private static long CountJson(Action<Utf8JsonWriter> write)
    {
        var counter = new CountingBufferWriter();
        using (var writer = new Utf8JsonWriter(counter, GeneralWriterOptions))
        {
            write(writer);
        }

        return counter.Count;
    }

    /// <summary>
    /// 反序列化提取信封中必需的业务载荷对象，解析失败时抛出分类异常。
    /// </summary>
    /// <typeparam name="T">期望的消息体类型。</typeparam>
    /// <param name="envelope">信封实例。</param>
    /// <param name="jsonTypeInfo">目标类型的 JSON 元数据。</param>
    /// <returns>反序列化后的业务消息体实例。</returns>
    private static T RequireBody<T>(IpcEnvelope envelope, JsonTypeInfo<T> jsonTypeInfo)
    {
        if (envelope.Payload is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"消息 {envelope.Type} 缺少对象格式的 payload 载荷。", nameof(envelope));
        }

        try
        {
            return JsonSerializer.Deserialize(element, jsonTypeInfo)
                ?? throw new ArgumentException($"消息 {envelope.Type} 的 payload 不能为空。", nameof(envelope));
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"消息 {envelope.Type} 的 payload 格式解析失败。", nameof(envelope), exception);
        }
    }
}
