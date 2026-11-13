using Ability.Sdk;

namespace Ability.Role;

/// <summary>
/// 执行上下文角色接口，为需要精细流程管控（重试、超时、取消侦听）的代码提供取消信号、时间提供者与进度汇报句柄。
/// </summary>
public interface IExecutionContext
{
    /// <summary>
    /// 本次执行的取消令牌。
    /// </summary>
    CancellationToken CancellationToken { get; }

    /// <summary>
    /// 本次执行采用的时间提供者。
    /// </summary>
    TimeProvider TimeProvider { get; }

    /// <summary>
    /// 进度汇报接收端。
    /// </summary>
    IProgress<RunProgress>? Progress { get; }
}
