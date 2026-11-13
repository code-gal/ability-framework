using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Ability.Core;

/// <summary>
/// 提供 TaskInput 的有界准备与完整帧编码。
/// </summary>
public static partial class IpcFrame
{
    /// <summary>
    /// 首遍 TaskInput 输入表示的最大提交字节数；这是准备空间上限，不是线上载荷限制。
    /// </summary>
    private const int MaxTaskInputPreparationBytes = 16 * 1024 * 1024;

    /// <summary>
    /// 首遍 TaskInput 输入表示的最大写出工作空间。
    /// </summary>
    private const int MaxTaskInputPreparationWorkspaceBytes = 128 * 1024 * 1024;

    /// <summary>
    /// 使用调用方模型元数据准备 TaskInput 完整帧，并在返回前按通用线编码规则核验最终载荷与信封。
    /// 业务模型只序列化一次；中间表示采用独立的有限准备空间上限。
    /// </summary>
    /// <typeparam name="TInput">任务输入模型类型。</typeparam>
    /// <param name="taskId">所属任务 ID。</param>
    /// <param name="taskTempPath">宿主分配的专属临时目录绝对路径。</param>
    /// <param name="payloadType">业务输入类型名提示。</param>
    /// <param name="input">待发送的业务输入模型。</param>
    /// <param name="jsonTypeInfo">调用方提供的输入模型 JSON 元数据。</param>
    /// <param name="traceParent">可选的 W3C traceparent。</param>
    /// <param name="traceState">可选的 W3C tracestate。</param>
    /// <returns>包含 4 字节长度前缀与完整 TaskInput 信封的新帧字节数组。</returns>
    /// <exception cref="ArgumentNullException">当必需引用参数为空时抛出。</exception>
    /// <exception cref="PayloadTooLargeException">当准备表示或最终业务载荷、信封超限时抛出。</exception>
    public static byte[] EncodeTaskInputFrame<TInput>(
        string taskId,
        string taskTempPath,
        string payloadType,
        TInput input,
        JsonTypeInfo<TInput> jsonTypeInfo,
        string? traceParent = null,
        string? traceState = null)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(taskTempPath);
        ArgumentNullException.ThrowIfNull(payloadType);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        using var inputDocument = SerializeTaskInputModelBounded(input, jsonTypeInfo);
        var businessPayload = inputDocument.RootElement;
        ValidateJsonElementBounded(
            businessPayload,
            MaxPayloadBytes,
            MaxEncodeWorkspaceBytes,
            ProtocolConstants.MessageTypes.TaskInput,
            isReceiving: false);

        var body = new TaskInputBody(taskId, taskTempPath, payloadType, businessPayload);
        using var bodyDocument = SerializeTaskInputBodyBounded(body);

        return EncodeFrame(new IpcEnvelope
        {
            Type = ProtocolConstants.MessageTypes.TaskInput,
            Payload = bodyDocument.RootElement,
            TraceParent = traceParent,
            TraceState = traceState
        });
    }

    /// <summary>
    /// 以调用方元数据和编码器将业务模型紧凑写入有限准备缓冲，再按模型的实际最大深度解析。
    /// </summary>
    /// <typeparam name="TInput">任务输入模型类型。</typeparam>
    /// <param name="input">待序列化的业务输入模型。</param>
    /// <param name="jsonTypeInfo">调用方提供的输入模型 JSON 元数据。</param>
    /// <returns>解析后的业务 JSON 文档。</returns>
    private static JsonDocument SerializeTaskInputModelBounded<TInput>(
        TInput input,
        JsonTypeInfo<TInput> jsonTypeInfo)
    {
        var buffer = new BoundedBufferWriter(
            MaxTaskInputPreparationBytes,
            MaxTaskInputPreparationWorkspaceBytes);

        try
        {
            using var writer = new Utf8JsonWriter(buffer, CreateCompactWriterOptions(jsonTypeInfo));
            JsonSerializer.Serialize(writer, input, jsonTypeInfo);
            writer.Flush();

            return JsonDocument.Parse(
                buffer.WrittenMemory,
                new JsonDocumentOptions { MaxDepth = jsonTypeInfo.Options.MaxDepth });
        }
        catch (PayloadTooLargeException exception)
        {
            throw new PayloadTooLargeException(
                $"TaskInput 输入准备超过 {MaxTaskInputPreparationBytes} 字节输出或 {MaxTaskInputPreparationWorkspaceBytes} 字节工作区限制：{exception.Message}");
        }
    }

    /// <summary>
    /// 将已解析的 TaskInput 消息体按 Core 通用编码规则写入有界中间缓冲。
    /// </summary>
    /// <param name="body">包含业务载荷与任务元数据的消息体。</param>
    /// <returns>解析后的 TaskInput 消息体文档。</returns>
    private static JsonDocument SerializeTaskInputBodyBounded(TaskInputBody body)
    {
        var buffer = new BoundedBufferWriter(MaxEnvelopeBytes, MaxEnvelopeWorkspaceBytes);
        try
        {
            using var writer = new Utf8JsonWriter(buffer, GeneralWriterOptions);
            JsonSerializer.Serialize(writer, body, ProtocolJsonContext.Default.TaskInputBody);
            writer.Flush();

            return JsonDocument.Parse(
                buffer.WrittenMemory,
                new JsonDocumentOptions { MaxDepth = ProtocolJsonContext.Default.Options.MaxDepth });
        }
        catch (PayloadTooLargeException exception)
        {
            throw new PayloadTooLargeException(
                $"TaskInput 消息体准备超过信封 {MaxEnvelopeBytes} 字节输出或 {MaxEnvelopeWorkspaceBytes} 字节工作区限制：{exception.Message}");
        }
    }

    /// <summary>
    /// 复制调用方 JSON 元数据的编码与深度设置，并关闭缩进以限制首遍表示的额外空白。
    /// </summary>
    /// <typeparam name="TInput">任务输入模型类型。</typeparam>
    /// <param name="jsonTypeInfo">调用方提供的输入模型 JSON 元数据。</param>
    /// <returns>保留调用方编码器和深度配置的紧凑写出器选项。</returns>
    private static JsonWriterOptions CreateCompactWriterOptions<TInput>(JsonTypeInfo<TInput> jsonTypeInfo)
    {
        var options = jsonTypeInfo.Options;
        return new JsonWriterOptions
        {
            Encoder = options.Encoder,
            Indented = false,
            IndentCharacter = options.IndentCharacter,
            IndentSize = options.IndentSize,
            MaxDepth = options.MaxDepth,
            NewLine = options.NewLine
        };
    }
}
