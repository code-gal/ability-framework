namespace Ability.Sdk;

/// <summary>
/// 代表已从宿主成功取得的受管资源租约句柄。
/// 租约对象实现 <see cref="IDisposable"/> 接口，调用方通过 using 作用域在业务 I/O 完成后触发同步释放，
/// 释放指令将排入通信调度队列，确保先于最终结果消息写出。
/// </summary>
public sealed class AbilityLease : IDisposable
{
    /// <summary>
    /// 宿主分配的租约全局唯一标识。
    /// </summary>
    private readonly string _leaseId;

    /// <summary>
    /// 释放当前租约时的回调调度动作。
    /// </summary>
    private readonly Action<string> _releaseAction;

    /// <summary>
    /// 租约释放状态标识（0 为未释放，1 为已释放）。
    /// </summary>
    private int _disposed;

    /// <summary>
    /// 初始化租约句柄。
    /// </summary>
    /// <param name="leaseId">宿主分配的唯一租约编号。</param>
    /// <param name="releaseAction">释放租约时的调度回调动作。</param>
    internal AbilityLease(string leaseId, Action<string> releaseAction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
        ArgumentNullException.ThrowIfNull(releaseAction);

        _leaseId = leaseId;
        _releaseAction = releaseAction;
    }

    /// <summary>
    /// 宿主分配的租约全局唯一标识符。
    /// </summary>
    public string LeaseId => _leaseId;

    /// <summary>
    /// 同步释放当前租约。
    /// 将 LeaseRelease 帧入队出站队列并由底层写循环写出；多次调用保证幂等，仅首次释放生效。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _releaseAction(_leaseId);
        }
    }
}
