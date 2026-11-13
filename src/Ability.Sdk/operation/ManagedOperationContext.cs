namespace Ability.Sdk;

/// <summary>
/// 传递给受管操作单次尝试执行委托的运行上下文。
/// 仅暴露本次尝试专属的局部取消令牌、时间源、进度报告端点、尝试序号及超期判定状态。
/// </summary>
public sealed class ManagedOperationContext
{
    /// <summary>
    /// 读取本次尝试是否已触发超期的委托。
    /// </summary>
    private readonly Func<bool> _deadlineExpiredAccessor;

    /// <summary>
    /// 初始化操作上下文。
    /// </summary>
    /// <param name="cancellationToken">本次尝试专属的取消令牌（涵盖调用方取消、根任务取消与局部超时）。</param>
    /// <param name="timeProvider">注入的时间提供者。</param>
    /// <param name="progress">本次尝试绑定的进度汇报接口。</param>
    /// <param name="attempt">本次尝试的序号（从 1 开始）。</param>
    /// <param name="deadlineExpiredAccessor">读取本次尝试是否已触发超期的委托。</param>
    internal ManagedOperationContext(
        CancellationToken cancellationToken,
        TimeProvider timeProvider,
        IProgress<RunProgress> progress,
        int attempt,
        Func<bool> deadlineExpiredAccessor)
    {
        CancellationToken = cancellationToken;
        TimeProvider = timeProvider ?? TimeProvider.System;
        Progress = progress;
        Attempt = attempt;
        _deadlineExpiredAccessor = deadlineExpiredAccessor ?? (() => false);
    }

    /// <summary>
    /// 本次尝试的有效取消令牌。
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// 时间提供者。
    /// </summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>
    /// 绑定到当前尝试代次的进度汇报接口。
    /// </summary>
    public IProgress<RunProgress> Progress { get; }

    /// <summary>
    /// 当前尝试的序号（首次尝试为 1）。
    /// </summary>
    public int Attempt { get; }

    /// <summary>
    /// 获取当前尝试是否已触发超时到期。
    /// 由操作内部状态机判定，业务委托只读访问。
    /// </summary>
    public bool DeadlineExpired => _deadlineExpiredAccessor();
}
