namespace Ability.Core;

/// <summary>
/// 结果流协议状态枚举。
/// </summary>
public enum ResultStreamStatus
{
    /// <summary>
    /// 流尚未开启。
    /// </summary>
    None = 0,

    /// <summary>
    /// 流已开启，正在按额度传输流元素。
    /// </summary>
    Open = 1,

    /// <summary>
    /// 流已正式关闭并完成总数对账。
    /// </summary>
    Closed = 2,

    /// <summary>
    /// 流协议对账或状态违规，处于损坏故障状态。
    /// </summary>
    Faulted = 3
}

/// <summary>
/// 结果流协议状态与信用额度管理器，统一覆盖生产端与消费端的状态流转与超额校验。
/// </summary>
public sealed class ResultStreamState : IDisposable
{
    /// <summary>生产端可用额度信号量。</summary>
    private readonly SemaphoreSlim _credits;
    /// <summary>协调额度登记、额度操作和停止状态。</summary>
    private readonly object _lifecycleLock = new();
    /// <summary>仅供额度等待者感知状态停止的内部取消源。</summary>
    private readonly CancellationTokenSource _stopCts = new();
    /// <summary>等待内部停止取消操作实际返回的信号。</summary>
    private readonly TaskCompletionSource _stopCancellationExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>所有已登记额度使用者退出且内部资源释放后的信号。</summary>
    private readonly TaskCompletionSource _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>累计核准额度计数。</summary>
    private long _totalCreditsGranted;
    /// <summary>累计传输或接收元素计数。</summary>
    private long _itemCount;
    /// <summary>累计业务消费元素计数。</summary>
    private long _consumedItemCount;
    /// <summary>当前协议状态原子值。</summary>
    private int _status = (int)ResultStreamStatus.None;
    /// <summary>已登记且尚未退出的额度等待者数量。受 <see cref="_lifecycleLock"/> 保护。</summary>
    private int _activeCreditWaiters;
    /// <summary>阻止新额度使用并终结既有等待的原因。受 <see cref="_lifecycleLock"/> 保护。</summary>
    private Exception? _stopFailure;
    /// <summary>内部停止取消是否已实际返回。受 <see cref="_lifecycleLock"/> 保护。</summary>
    private bool _stopCancellationReturned;
    /// <summary>内部停止取消或同步资源释放期间的异常。受 <see cref="_lifecycleLock"/> 保护。</summary>
    private Exception? _cleanupFailure;
    /// <summary>结果流同步资源是否已释放。受 <see cref="_lifecycleLock"/> 保护。</summary>
    private bool _resourcesReleased;

    /// <summary>
    /// 初始化结果流状态。
    /// </summary>
    /// <param name="initialCreditWindow">初始可用额度窗口大小。</param>
    public ResultStreamState(int initialCreditWindow = 0)
    {
        var initial = Math.Max(0, initialCreditWindow);
        _credits = new SemaphoreSlim(initial);
        _totalCreditsGranted = initial;

        _ = _exitTcs.Task.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// 当前流协议状态。
    /// </summary>
    public ResultStreamStatus Status => (ResultStreamStatus)Volatile.Read(ref _status);

    /// <summary>
    /// 流是否已经开启过（包括正在开启、已关闭或故障态）。
    /// </summary>
    public bool HasStreamStarted => Status != ResultStreamStatus.None;

    /// <summary>
    /// 当前累计传输或接收的元素总数（真值计数）。
    /// </summary>
    public long ItemCount => Interlocked.Read(ref _itemCount);

    /// <summary>
    /// 当前已完成业务消费的流元素总数。
    /// </summary>
    public long ConsumedItemCount => Interlocked.Read(ref _consumedItemCount);

    /// <summary>
    /// 累计向生产端发放的信用额度总数。
    /// </summary>
    public long TotalCreditsGranted => Interlocked.Read(ref _totalCreditsGranted);

    /// <summary>
    /// 获取额度等待者退出及内部资源释放完成的信号。
    /// </summary>
    internal Task ExitTask => _exitTcs.Task;

    /// <summary>
    /// 标记流正式开启（状态从 None 迁移为 Open）。生产端使用。
    /// </summary>
    /// <exception cref="ProtocolException">当流已开启或处于非 None 状态时抛出。</exception>
    public void MarkOpen()
    {
        if (Interlocked.CompareExchange(ref _status, (int)ResultStreamStatus.Open, (int)ResultStreamStatus.None) != (int)ResultStreamStatus.None)
        {
            throw new ProtocolException($"结果流状态错误：无法在状态 {Status} 下重复开启流。");
        }
    }

    /// <summary>
    /// 接收端在底层物理收到 ResultOpen 帧时严格核验并转换协议状态（None -> Open）。
    /// 由 CompareExchange 原子核验并转换，保证只成功一次。
    /// </summary>
    /// <exception cref="ProtocolException">当流已开启或已处于异常状态时抛出。</exception>
    public void ValidateAndRecordOpen()
    {
        if (Interlocked.CompareExchange(ref _status, (int)ResultStreamStatus.Open, (int)ResultStreamStatus.None) == (int)ResultStreamStatus.None)
        {
            return;
        }

        Volatile.Write(ref _status, (int)ResultStreamStatus.Faulted);
        throw new ProtocolException($"结果流协议违规：在状态 {Status} 下收到重复或非预期的 ResultOpen 帧。");
    }

    /// <summary>
    /// 补充信用额度（例如收到宿主下发的 ResultCredit 帧）。
    /// </summary>
    /// <param name="grantCount">新增的额度点数。</param>
    /// <exception cref="ArgumentOutOfRangeException">当额度小于等于 0 时抛出。</exception>
    public void GrantCredit(int grantCount)
    {
        if (grantCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(grantCount), "补充的额度必须为正数。");
        }

        lock (_lifecycleLock)
        {
            ThrowIfStoppedLocked();
            Interlocked.Add(ref _totalCreditsGranted, grantCount);
            try
            {
                _credits.Release(grantCount);
            }
            catch
            {
                Interlocked.Add(ref _totalCreditsGranted, -grantCount);
                throw;
            }
        }
    }

    /// <summary>
    /// 归还未使用的额度（例如流结束且未产出更多元素时）。
    /// </summary>
    /// <param name="count">归还额度数量。</param>
    public void ReturnCredit(int count = 1)
    {
        if (count <= 0)
        {
            return;
        }

        lock (_lifecycleLock)
        {
            if (_stopFailure is not null)
            {
                return;
            }

            _credits.Release(count);
        }
    }

    /// <summary>
    /// 撤回未成功发出的信用额度（例如信用帧写入失败时回滚本地额度计数）。
    /// </summary>
    /// <param name="count">撤回额度点数。</param>
    public void RevokeCredit(int count = 1)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _totalCreditsGranted, -count);
    }

    /// <summary>
    /// 等待并获取 1 点发送额度。
    /// </summary>
    /// <param name="cancellationToken">等待取消令牌。</param>
    /// <returns>成功获取额度时返回的任务。</returns>
    public async Task AcquireCreditAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource linkedCts;
        CancellationToken stopToken;
        lock (_lifecycleLock)
        {
            ThrowIfStoppedLocked();
            _activeCreditWaiters++;
            stopToken = _stopCts.Token;
            try
            {
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopToken);
            }
            catch
            {
                _activeCreditWaiters--;
                throw;
            }
        }

        try
        {
            await _credits.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            lock (_lifecycleLock)
            {
                ThrowIfStoppedLocked();
            }
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Exception? stopFailure;
            lock (_lifecycleLock)
            {
                stopFailure = _stopFailure;
            }

            if (stopFailure is not null)
            {
                throw CreateStoppedException(stopFailure);
            }

            throw;
        }
        finally
        {
            if (stopToken.IsCancellationRequested)
            {
                await _stopCancellationExited.Task.ConfigureAwait(false);
            }

            linkedCts.Dispose();
            lock (_lifecycleLock)
            {
                _activeCreditWaiters--;
                TryCompleteExitLocked();
            }
        }
    }

    /// <summary>
    /// 记录已发送一个结果项，并递增内部计数器。
    /// </summary>
    /// <returns>递增后的最新元素总数。</returns>
    public long RecordItem()
    {
        return Interlocked.Increment(ref _itemCount);
    }

    /// <summary>
    /// 接收端底层物理收帧时即时校验接收到一个流元素：检查流是否处于开启态，并核查是否超出累计授予的额度上限。
    /// 状态为已开流时先递增计数，再按累计授予量判定额度。
    /// </summary>
    /// <returns>接收后的最新元素计数。</returns>
    /// <exception cref="ProtocolException">当流未开启或超出额度窗口时抛出。</exception>
    public long ValidateAndRecordReceivedItem()
    {
        var status = Status;
        var count = status == ResultStreamStatus.Open ? Interlocked.Increment(ref _itemCount) : ItemCount;
        if (status == ResultStreamStatus.Open && count <= TotalCreditsGranted)
        {
            return count;
        }

        Volatile.Write(ref _status, (int)ResultStreamStatus.Faulted);
        throw status != ResultStreamStatus.Open
            ? new ProtocolException($"结果流协议违规：在状态 {Status} 下收到非预期的流元素。")
            : new ProtocolException($"结果流协议违规：技能端到达的元素数 ({count}) 超出当时已授权额度 ({TotalCreditsGranted})。");
    }

    /// <summary>
    /// 记录已成功完成一个流元素的业务消费。仅在消费回调成功后调用。
    /// </summary>
    /// <returns>消费后的最新元素计数。</returns>
    public long RecordConsumedItem()
    {
        return Interlocked.Increment(ref _consumedItemCount);
    }

    /// <summary>
    /// 接收端底层物理收帧时校验并记录 ResultClose 帧。
    /// </summary>
    /// <exception cref="ProtocolException">当流未处于开启状态时抛出。</exception>
    public void ValidateAndRecordClose()
    {
        if (Status != ResultStreamStatus.Open)
        {
            Volatile.Write(ref _status, (int)ResultStreamStatus.Faulted);
            throw new ProtocolException($"结果流协议违规：在状态 {Status} 下收到非预期的 ResultClose 帧。");
        }

        Volatile.Write(ref _status, (int)ResultStreamStatus.Closed);
    }

    /// <summary>
    /// 标记流已关闭，并在变更状态前核验报告的总数与本地实际统计数量是否一致。
    /// 仅在对账完全一致时迁移至 Closed；不一致时标记为 Faulted 并抛出异常。
    /// </summary>
    /// <param name="expectedTotalCount">协议声明的元素总数。</param>
    /// <exception cref="ProtocolException">当流未处于开启状态，或声明总数与实际不符时抛出。</exception>
    public void MarkClosed(long expectedTotalCount)
    {
        if (Status != ResultStreamStatus.Open && Status != ResultStreamStatus.Closed)
        {
            throw new ProtocolException($"结果流状态错误：无法在状态 {Status} 下关闭流。");
        }

        var actualCount = ItemCount;
        if (actualCount != expectedTotalCount)
        {
            Volatile.Write(ref _status, (int)ResultStreamStatus.Faulted);
            throw new ProtocolException($"结果流对账失败：协议声明总数 {expectedTotalCount} 与本地实际计数 {actualCount} 不一致。");
        }

        Volatile.Write(ref _status, (int)ResultStreamStatus.Closed);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        StopCreditWaits(new ObjectDisposedException(nameof(ResultStreamState), "结果流状态已释放。"));
    }

    /// <summary>
    /// 停止接纳额度操作并通知已登记的等待者退出。
    /// </summary>
    /// <param name="failure">等待者应观察到的连接或状态结束原因。</param>
    internal void StopCreditWaits(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        lock (_lifecycleLock)
        {
            if (_stopFailure is not null)
            {
                return;
            }

            _stopFailure = failure;
        }

        Exception? cancellationFailure = null;
        try
        {
            _stopCts.Cancel();
        }
        catch (Exception ex)
        {
            cancellationFailure = ex;
        }
        finally
        {
            lock (_lifecycleLock)
            {
                _stopCancellationReturned = true;
                _cleanupFailure ??= cancellationFailure;
                _stopCancellationExited.TrySetResult();
                TryCompleteExitLocked();
            }
        }
    }

    /// <summary>
    /// 若当前状态已停止则按停止原因拒绝额度操作。
    /// </summary>
    private void ThrowIfStoppedLocked()
    {
        if (_stopFailure is ObjectDisposedException disposedException)
        {
            throw new ObjectDisposedException(nameof(ResultStreamState), disposedException.Message);
        }

        if (_stopFailure is not null)
        {
            throw CreateStoppedException(_stopFailure);
        }
    }

    /// <summary>
    /// 为一次额度等待构造独立的状态停止异常。
    /// </summary>
    /// <param name="failure">连接或状态停止原因。</param>
    /// <returns>表示结果流已停止的异常实例。</returns>
    private static Exception CreateStoppedException(Exception failure)
    {
        if (failure is ObjectDisposedException disposedException)
        {
            return new ObjectDisposedException(nameof(ResultStreamState), disposedException.Message);
        }

        return new IOException("结果流所属连接已关闭，无法继续等待信用额度。", failure);
    }

    /// <summary>
    /// 在停止取消已返回且所有额度等待者实际退出后释放内部同步资源并完成退出信号。
    /// </summary>
    private void TryCompleteExitLocked()
    {
        if (_resourcesReleased || !_stopCancellationReturned || _activeCreditWaiters != 0)
        {
            return;
        }

        _resourcesReleased = true;
        try
        {
            _credits.Dispose();
        }
        catch (Exception ex)
        {
            _cleanupFailure ??= ex;
        }

        try
        {
            _stopCts.Dispose();
        }
        catch (Exception ex)
        {
            _cleanupFailure ??= ex;
        }

        if (_cleanupFailure is null)
        {
            _exitTcs.TrySetResult();
        }
        else
        {
            _exitTcs.TrySetException(_cleanupFailure);
        }
    }
}
