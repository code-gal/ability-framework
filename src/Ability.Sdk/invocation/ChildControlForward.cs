using Ability.Core;

namespace Ability.Sdk;

/// <summary>
/// 待转发给子调用的控制消息包装对象，记录目标子调用的写出委托与内层协议信封。
/// </summary>
/// <param name="Control">子调用的控制消息发送委托，支持传入取消令牌。</param>
/// <param name="Message">待写入子管道的内层协议信封。</param>
internal sealed record ChildControlForward(Func<IpcEnvelope, CancellationToken, Task> Control, IpcEnvelope Message);
