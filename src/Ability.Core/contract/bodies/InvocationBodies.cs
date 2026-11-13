namespace Ability.Core;

/// <summary>
/// 启动子调用进程请求体；由父调用向宿主申请启动子 Launcher。
/// </summary>
/// <param name="InvocationKey">本次子调用的唯一调用键。</param>
/// <param name="ParentInvocationKey">发起方的父调用键。</param>
/// <param name="TaskId">所属任务 ID。</param>
/// <param name="LauncherPath">目标子 Launcher 的入口路径或标识。</param>
public sealed record ChildStartBody(
    string InvocationKey,
    string ParentInvocationKey,
    string TaskId,
    string LauncherPath);

/// <summary>
/// 子进程已启动确认体；由宿主向父调用确认启动结果并交付实际进程 ID。
/// </summary>
/// <param name="InvocationKey">子调用的唯一调用键。</param>
/// <param name="TaskId">所属任务 ID。</param>
/// <param name="ProcessId">宿主启动的子进程操作系统 PID。</param>
public sealed record ChildStartedBody(
    string InvocationKey,
    string TaskId,
    int ProcessId);

/// <summary>
/// 请求停止子进程体；由父调用向宿主发起，附带停止原因。
/// </summary>
/// <param name="InvocationKey">目标子调用的调用键。</param>
/// <param name="ParentInvocationKey">发起方的父调用键，用于宿主校验归属。</param>
/// <param name="Reason">停止的原因说明。</param>
public sealed record ChildStopBody(
    string InvocationKey,
    string ParentInvocationKey,
    string Reason);

/// <summary>
/// 子进程已回收确认体；宿主在确认子进程完全退出并销账租约后上报。
/// </summary>
/// <param name="InvocationKey">子调用的调用键。</param>
/// <param name="ExitCode">进程的实际退出码（若有）。</param>
/// <param name="CleanedLeaseCount">随进程退出集中清理的剩余租约数量。</param>
/// <param name="Outcome">回收结果说明（如 "Exited"、"Killed"）。</param>
/// <param name="ExitedNaturally">是否为未受干涉的自然退出。</param>
public sealed record ChildReapedBody(
    string InvocationKey,
    int? ExitCode,
    int CleanedLeaseCount,
    string Outcome,
    bool ExitedNaturally = false);

/// <summary>
/// 转发给子调用的内层信封消息体；外层调用键标明路由归属。
/// </summary>
/// <param name="InvocationKey">目标子调用的调用键。</param>
/// <param name="Message">转发的内层 IPC 信封。</param>
public sealed record ChildMessageBody(
    string InvocationKey,
    IpcEnvelope Message);

/// <summary>
/// 转发消息的答复体；承载子调用回复的内层信封。
/// </summary>
/// <param name="InvocationKey">子调用的调用键。</param>
/// <param name="Response">内层答复信封；为空表示该消息不产生答复。</param>
public sealed record ChildMessageReplyBody(
    string InvocationKey,
    IpcEnvelope? Response);
