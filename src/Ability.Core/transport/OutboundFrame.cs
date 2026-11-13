namespace Ability.Core;

/// <summary>
/// 待写出战帧：持有编码字节、队列名额所有权及写出通知完成源。
/// </summary>
internal sealed class OutboundFrame
{
    /// <summary>占用的容量信号量。</summary>
    private readonly SemaphoreSlim _slotSemaphore;
    /// <summary>名额归还回调。</summary>
    private readonly Action? _onSlotReleased;
    /// <summary>是否允许写入的原子裁决委托。</summary>
    private readonly Func<bool>? _shouldWrite;
    /// <summary>即将向底层流写出前的回调。</summary>
    private readonly Action? _onWriting;
    /// <summary>完成写出并刷新后的回调。</summary>
    private readonly Action? _onSent;
    /// <summary>名额是否已归还的原子标记。</summary>
    private int _slotReleased;

    /// <summary>
    /// 初始化待写出战帧。
    /// </summary>
    /// <param name="frameBytes">已完成协议编码并附加长度前缀的完整二进制数据。</param>
    /// <param name="slotSemaphore">该帧占用的队列名额信号量。</param>
    /// <param name="isResultFrame">是否为流式结果帧（决定其所属队列与保序策略）。</param>
    /// <param name="onSlotReleased">名额归还时的回调（用于更新待写计数与空闲信号）。</param>
    /// <param name="shouldWrite">是否允许开始写入的原子裁决委托。</param>
    /// <param name="onWriting">即将向底层流执行写入前的回调。</param>
    /// <param name="onSent">完成写入并 Flush 后的回调。</param>
    public OutboundFrame(
        byte[] frameBytes,
        SemaphoreSlim slotSemaphore,
        bool isResultFrame,
        Action? onSlotReleased = null,
        Func<bool>? shouldWrite = null,
        Action? onWriting = null,
        Action? onSent = null)
    {
        FrameBytes = frameBytes;
        _slotSemaphore = slotSemaphore;
        IsResultFrame = isResultFrame;
        _onSlotReleased = onSlotReleased;
        _shouldWrite = shouldWrite;
        _onWriting = onWriting;
        _onSent = onSent;
        Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// 完整帧二进制内容。
    /// </summary>
    public byte[] FrameBytes { get; }

    /// <summary>
    /// 是否属于结果队列帧。
    /// </summary>
    public bool IsResultFrame { get; }

    /// <summary>
    /// 本帧写出或中止的完成通知源。
    /// </summary>
    public TaskCompletionSource Completion { get; }

    /// <summary>
    /// 尝试取得开始向底层流写入数据的权利。
    /// 与写入前取消执行同一次原子裁决。若调用方已先于写入取消或放弃，则返回 false，通知写循环跳过输出。
    /// </summary>
    /// <returns>若取得写出权利返回 true；若已被取消或放弃返回 false。</returns>
    public bool TryBeginWrite()
    {
        if (Completion.Task.IsCompleted)
        {
            return false;
        }

        if (_shouldWrite is not null && !_shouldWrite())
        {
            return false;
        }

        _onWriting?.Invoke();
        return true;
    }

    /// <summary>
    /// 通知数据已成功写出并刷新至底层流。
    /// </summary>
    public void NotifySent() => _onSent?.Invoke();

    /// <summary>
    /// 外部调用方取消等待时放弃本帧。
    /// 写入循环检测到已放弃状态将跳过向底层流写出，并在离开队列时归还名额。
    /// </summary>
    /// <param name="cancellationToken">触发取消的令牌。</param>
    public void Abandon(CancellationToken cancellationToken)
    {
        Completion.TrySetCanceled(cancellationToken);
    }

    /// <summary>
    /// 归还本帧占用的队列名额。保证幂等执行，仅在首次调用时释放信号量与计数。
    /// </summary>
    public void ReleaseSlot()
    {
        if (Interlocked.Exchange(ref _slotReleased, 1) != 0)
        {
            return;
        }

        try
        {
            _slotSemaphore.Release();
        }
        catch (ObjectDisposedException)
        {
            // 队列若已提前释放，忽略对已注销信号量的访问
        }

        _onSlotReleased?.Invoke();
    }
}
