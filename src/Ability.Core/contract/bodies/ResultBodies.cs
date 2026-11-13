using System.Text.Json;

namespace Ability.Core;

/// <summary>
/// 一元调用结果提交体；为调用的终态输出，载荷受 1 MiB 限制。
/// </summary>
/// <param name="PayloadType">结果业务模型的类型名。</param>
/// <param name="Payload">以原始 JSON 表达的业务结果。</param>
public sealed record SubmitResultBody(
    string PayloadType,
    JsonElement Payload);

/// <summary>
/// 终态业务或协议故障体；表达未处理异常或不可恢复的执行失败。
/// </summary>
/// <param name="Kind">故障类别简短标识（UTF-8 最多截断至 256 字节）。</param>
/// <param name="Message">人可读的故障说明（UTF-8 最多截断至 4096 字节）。</param>
/// <param name="Site">发生故障的位置或组件标识（UTF-8 最多截断至 256 字节）。</param>
public sealed record SubmitFaultBody(
    string Kind,
    string Message,
    string Site);

/// <summary>
/// 针对具体请求的错误或拒绝答复体；必须带回被答复请求的原 ID。
/// </summary>
/// <param name="Kind">错误类型标识（UTF-8 最多截断至 128 字节）。</param>
/// <param name="Message">错误详细说明（UTF-8 最多截断至 2048 字节）。</param>
public sealed record RequestFaultBody(
    string Kind,
    string Message);

/// <summary>
/// 开启流式结果声明体；声明随后流元素的类型名。
/// </summary>
/// <param name="PayloadType">流元素的业务模型类型名。</param>
public sealed record ResultOpenBody(string PayloadType);

/// <summary>
/// 单个流式结果元素体；承载元素数据并消耗 1 点额度。
/// </summary>
/// <param name="Payload">以原始 JSON 表达的单个流元素。</param>
public sealed record ResultItemBody(JsonElement Payload);

/// <summary>
/// 流元素发送额度补发体；由上游向下游发放允许发送的元素条数。
/// </summary>
/// <param name="Count">本次补发的额度数量（必须大于 0）。</param>
public sealed record ResultCreditBody(int Count);

/// <summary>
/// 结果流结束汇总对账体；宿主在收到 ResultClose 后回复，用于确认实际接收的元素总量。
/// </summary>
/// <param name="ItemCount">宿主实际确认接收的流元素总数。</param>
public sealed record ResultClosedBody(int ItemCount);
