namespace Ability.Hosting;

/// <summary>
/// 宿主单次运行调用的借用依赖使用结束观察句柄。
/// 由调用方在调用前创建或由 Hosting 缺省创建，通过 <see cref="Completion"/> 表达所有后台使用者及派生清理已实际退出。
/// </summary>
public sealed class HostRunLifetime
{
    /// <summary>
    /// 承载完成信号的异步源，仅在全部使用者退出并发布报告后置为结果。
    /// </summary>
    private readonly TaskCompletionSource<HostCleanupReport> _completionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 记录绑定状态的原子标记，0 为未绑定，1 为已绑定。
    /// </summary>
    private int _bound;

    /// <summary>
    /// 观察实际使用结束的异步任务。当所有实际使用者、派生清理及自有资源释放实际结束后以成功状态返回最终清理报告。
    /// </summary>
    public Task<HostCleanupReport> Completion => _completionSource.Task;

    /// <summary>
    /// 尝试独占绑定本次运行调用。重复绑定将拒绝。
    /// </summary>
    /// <returns>若首次绑定成功返回 true；若已被绑定过则返回 false。</returns>
    internal bool TryBind()
    {
        return Interlocked.CompareExchange(ref _bound, 1, 0) == 0;
    }

    /// <summary>
    /// 发布最终收尾报告，标记实际使用结束。
    /// </summary>
    /// <param name="report">最终收尾报告。</param>
    internal void Complete(HostCleanupReport report)
    {
        _completionSource.TrySetResult(report);
    }
}

