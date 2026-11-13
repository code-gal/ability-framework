using System.Text.Json;
using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// 宿主端子消息多层路由上下文记录。
/// </summary>
/// <param name="OuterRequestId">外层代理或真实请求 ID。</param>
/// <param name="InvocationKey">发起转发的子调用键。</param>
/// <param name="InnerRequestId">内层真实请求 ID。</param>
/// <param name="ParentRoute">父级路由上下文。</param>
internal sealed record ChildMessageRoute(
    long OuterRequestId,
    string InvocationKey,
    long InnerRequestId,
    ChildMessageRoute? ParentRoute = null);

/// <summary>
/// 宿主端子消息路由与包装辅助工具类。
/// </summary>
internal static class ChildMessageRouter
{
    /// <summary>
    /// 将内层响应或通知确认逐层包装为 ChildMessageReply 信封。
    /// 若 response 为 null，则最内层回发不含负载的单向通知确认。
    /// </summary>
    /// <param name="route">子消息路由节点。</param>
    /// <param name="response">待包装的内层响应信封（通知确认为 null）。</param>
    /// <returns>最外层的回复信封。</returns>
    public static IpcEnvelope WrapChildResponse(ChildMessageRoute route, IpcEnvelope? response)
    {
        var current = route;
        var wrapped = response;

        while (current is not null)
        {
            wrapped = ProtocolMessages.CreateChildMessageReply(
                current.OuterRequestId,
                current.InvocationKey,
                wrapped);
            current = current.ParentRoute;
        }

        return wrapped!;
    }

    /// <summary>
    /// 校验目标调用是否为指定祖先调用的后代（包含直接子节点或深层孙节点）。
    /// </summary>
    /// <param name="registry">权威调用登记表。</param>
    /// <param name="record">目标调用记录。</param>
    /// <param name="ancestorKey">待核验的祖先调用键。</param>
    /// <returns>若是指定调用的后代返回 true，否则返回 false。</returns>
    public static bool IsDescendantOf(InvocationRegistry registry, InvocationRecord record, string ancestorKey)
    {
        var current = record;
        while (current is not null)
        {
            if (string.Equals(current.InvocationKey, ancestorKey, StringComparison.Ordinal))
            {
                return true;
            }

            if (current.ParentKey is null)
            {
                break;
            }

            if (!registry.TryGet(current.ParentKey, out var parent) && !registry.TryGetHistorical(current.ParentKey, out parent))
            {
                break;
            }

            current = parent;
        }

        return false;
    }

    /// <summary>
    /// 判断消息类型是否属于禁止通过 ChildMessage 转发的会话级/结果流结果帧。
    /// </summary>
    /// <param name="type">协议消息类型名称。</param>
    /// <returns>若禁止转发返回 true，否则返回 false。</returns>
    public static bool IsForbiddenForwardType(string type) => type is
        ProtocolConstants.MessageTypes.SubmitResult or
        ProtocolConstants.MessageTypes.ResultOpen or
        ProtocolConstants.MessageTypes.ResultItem or
        ProtocolConstants.MessageTypes.ResultClose or
        ProtocolConstants.MessageTypes.SubmitFault;

    /// <summary>
    /// 判断消息类型是否为对请求的双向答复帧（具有关联请求编号）。
    /// </summary>
    /// <param name="type">协议消息类型名称。</param>
    /// <returns>若是答复帧返回 true，否则返回 false。</returns>
    public static bool IsReplyType(string type) => type is
        ProtocolConstants.MessageTypes.PolicyApplied or
        ProtocolConstants.MessageTypes.RequestFault or
        ProtocolConstants.MessageTypes.OperationAccepted or
        ProtocolConstants.MessageTypes.LeaseGranted or
        ProtocolConstants.MessageTypes.LeaseDenied or
        ProtocolConstants.MessageTypes.ParamValue or
        ProtocolConstants.MessageTypes.SourceRepairResult or
        ProtocolConstants.MessageTypes.ChildStarted or
        ProtocolConstants.MessageTypes.ChildReaped or
        ProtocolConstants.MessageTypes.ChildMessageReply;
}

/// <summary>
/// 宿主执行协调结果快照模型。
/// </summary>
internal sealed class HostExecutionResult
{
    /// <summary>任务最终结果标识。</summary>
    public required string Outcome { get; init; }

    /// <summary>已接受的技能终态（SubmitResult、ResultClose 或 SubmitFault）；尚未接受终态时为 null。</summary>
    public string? BusinessOutcome { get; init; }

    /// <summary>输出类型名称。</summary>
    public string? OutputTypeName { get; init; }

    /// <summary>输出 JSON 载荷副本。</summary>
    public JsonElement? OutputJson { get; init; }

    /// <summary>流式接收的元素总数。</summary>
    public int StreamItemCount { get; init; }

    /// <summary>故障分类类型。</summary>
    public string? FaultKind { get; init; }

    /// <summary>故障描述文本。</summary>
    public string? FaultMessage { get; init; }

    /// <summary>观测到的消息类型列表。</summary>
    public required List<string> ObservedMessages { get; init; }

    /// <summary>观测消息列表是否已截断。</summary>
    public bool ObservedTruncated { get; init; }
}
