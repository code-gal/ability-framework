using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ability.Core;

/// <summary>
/// 一条 IPC 消息的协议信封；跨进程传递消息的统一外壳。
/// 业务载荷保持为原始 JSON，信封负责类型分派、请求答复关联与链路追踪。
/// </summary>
public sealed class IpcEnvelope
{
    /// <summary>
    /// 消息类型标识；接收方根据类型名称分派至对应的消息处理器。
    /// </summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// 请求与答复的关联编号；请求方生成此 ID，答复方必须在答复信封中带回相同 ID。
    /// 通知类消息此字段为 null。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Id { get; init; }

    /// <summary>
    /// W3C Trace Context 的 traceparent 标识；用于跨进程传递分布式追踪上下文。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TraceParent { get; init; }

    /// <summary>
    /// W3C Trace Context 的 tracestate 标识；承载供应商特定的追踪状态。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TraceState { get; init; }

    /// <summary>
    /// 业务载荷数据；在协议层保持为原始 JSON 元素，由具体业务或协议体单独解析。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Payload { get; init; }

    /// <summary>
    /// 是否为可忽略的扩展通知。为 true 时表示对端不认识该类型时可安全忽略。
    /// 仅允许出现在无 <see cref="Id"/> 的通知消息上。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool OptionalNotification { get; init; }
}
