using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Ability.Core;

/// <summary>
/// 出站调度队列：分别管理控制队列与结果队列名额，并在后台单一写循环中按控制优先与结果防饥饿原则串行写出。
/// </summary>
internal sealed class OutboundQueue : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// 控制帧每次连续写出的最大批次上限（防止流式结果帧饥饿）。
    /// </summary>
    private const int ControlBatchLimit = 8;

    /// <summary>
    /// 控制帧默认最大未决缓冲数量（名额上限）。
    /// </summary>
    public const int MaxPendingControlFrames = 64;

    /// <summary>
    /// 流式结果帧默认最大未决缓冲数量（名额上限）。
    /// </summary>
    public const int MaxPendingResultFrames = 4;

    /// <summary>控制帧名额上限。</summary>
    private readonly int _maxControlFrames;
    /// <summary>结果帧名额上限。</summary>
    private readonly int _maxResultFrames;
    /// <summary>控制帧容量信号量。</summary>
    private readonly SemaphoreSlim _controlSlots;
    /// <summary>结果帧容量信号量。</summary>
    private readonly SemaphoreSlim _resultSlots;

    /// <summary>控制帧无界通道。</summary>
    private readonly Channel<OutboundFrame> _controlFrames = Channel.CreateUnbounded<OutboundFrame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    /// <summary>结果帧无界通道。</summary>
    private readonly Channel<OutboundFrame> _resultFrames = Channel.CreateUnbounded<OutboundFrame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <summary>写循环就绪信号量。</summary>
    private readonly SemaphoreSlim _writeSignal = new(0);
    /// <summary>在途待写帧集合。</summary>
    private readonly ConcurrentDictionary<OutboundFrame, byte> _outstandingFrames = new();

    /// <summary>待写状态锁。</summary>
    private readonly object _pendingWriteLock = new();
    /// <summary>当前已入队待写帧计数。</summary>
    private int _pendingFrameCount;
    /// <summary>已接纳且尚未完成名额、编码、帧等待及取消注册清理的生产者计数。</summary>
    private int _activeProducers;
    /// <summary>全部待写帧完成写出的通知源。</summary>
    private TaskCompletionSource _writesIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>停止写操作取消源。</summary>
    private readonly CancellationTokenSource _shutdownCts = new();
    /// <summary>停止写操作取消真实完成信号源。</summary>
    private readonly TaskCompletionSource _shutdownTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>停止后全部生产者和待写帧退出的通知源。</summary>
    private readonly TaskCompletionSource _usersExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>全部使用者、取消工作及写循环物理退出的汇聚任务。</summary>
    private readonly Task _exitTask;
    /// <summary>写操作已停止标记。</summary>
    private int _writesStopped;
    /// <summary>资源已释放标记。</summary>
    private int _disposed;
    /// <summary>队列残留帧是否已排空标记（0 表示未排空，1 表示已排空）。</summary>
    private int _drained;
    /// <summary>释放操作同步锁。</summary>
    private readonly object _disposeLock = new();
    /// <summary>共享的队列释放任务。</summary>
    private Task? _disposeTask;
    /// <summary>调用方等待超时后仍由队列持有的同步原语释放任务。</summary>
    private Task? _primitiveDisposalTask;
    /// <summary>同步原语是否已完成释放标记。</summary>
    private bool _primitivesDisposed;
    /// <summary>写循环任务是否已安排调度标记（0 表示未安排，1 表示已安排）。</summary>
    private int _writeLoopScheduled;
    /// <summary>写循环已退出通知源。</summary>
    private readonly TaskCompletionSource _writeLoopExited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 初始化出站调度队列。
    /// </summary>
    /// <param name="maxControlFrames">控制队列名额容量（缺省 64）。</param>
    /// <param name="maxResultFrames">结果队列名额容量（缺省 4）。</param>
    public OutboundQueue(int maxControlFrames = 64, int maxResultFrames = 4)
    {
        _maxControlFrames = Math.Max(1, maxControlFrames);
        _maxResultFrames = Math.Max(1, maxResultFrames);
        _controlSlots = new SemaphoreSlim(_maxControlFrames, _maxControlFrames);
        _resultSlots = new SemaphoreSlim(_maxResultFrames, _maxResultFrames);
        _exitTask = Task.WhenAll(_usersExited.Task, _shutdownTcs.Task, _writeLoopExited.Task);
    }

    /// <summary>
    /// 获取当前队列中尚未完成写出的总帧数。
    /// </summary>
    public int PendingFrameCount
    {
        get
        {
            lock (_pendingWriteLock)
            {
                return _pendingFrameCount;
            }
        }
    }

    /// <summary>
    /// 出站写操作是否已被停止。
    /// </summary>
    public bool IsStopped => Volatile.Read(ref _writesStopped) != 0;

    /// <summary>
    /// 获取同步原语释放任务。
    /// </summary>
    internal Task? PrimitiveDisposalTask => _primitiveDisposalTask;

    /// <summary>
    /// 获取停止写操作取消后台任务的真实完成信号。
    /// 重复 Stop 与 Dispose 均观察此同一个真实任务。
    /// </summary>
    public Task ShutdownCancelTask => _shutdownTcs.Task;

    /// <summary>
    /// 获取停止后全部已接纳使用者、取消工作和写循环实际退出的信号。
    /// 任务故障表示使用者已退出，但取消工作发生了清理异常。
    /// </summary>
    public Task ExitTask => _exitTask;

    /// <summary>
    /// 获取写循环任务是否已被安排调度。
    /// </summary>
    public bool IsWriteLoopScheduled => Volatile.Read(ref _writeLoopScheduled) != 0;

    /// <summary>
    /// 登记写循环任务已成功安排调度。
    /// 保证队列释放流程识别任务已在执行或等待执行，不会误判为“从未启动”。
    /// </summary>
    public void RegisterWriteLoop()
    {
        lock (_pendingWriteLock)
        {
            if (_writeLoopScheduled != 0)
            {
                return;
            }

            ThrowIfStopped();
            _writeLoopScheduled = 1;
        }
    }

    /// <summary>
    /// 请求分配名额，编码并在写循环中异步发送一帧数据。
    /// </summary>
    /// <param name="isResultFrame">true 表示占用流式结果名额；false 表示占用控制队列名额。</param>
    /// <param name="encode">帧编码委托，在取得名额后才触发，防止过早分配大型缓冲。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <param name="shouldWrite">是否允许开始写出的原子裁决委托。</param>
    /// <param name="onWriting">即将写入底层流前的回调。</param>
    /// <param name="onSent">完成写入底层流后的回调。</param>
    /// <returns>当帧已成功写出至底层流（或被取消/抛错）时完成的任务。</returns>
    public async Task EnqueueFrameAsync(
        bool isResultFrame,
        Func<byte[]> encode,
        CancellationToken cancellationToken = default,
        Func<bool>? shouldWrite = null,
        Action? onWriting = null,
        Action? onSent = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 与停止共同裁决接纳；使用权覆盖编码、等待帧完成和取消注册清理。
        lock (_pendingWriteLock)
        {
            ThrowIfStopped();
            _activeProducers++;
        }

        var slots = isResultFrame ? _resultSlots : _controlSlots;
        var channel = isResultFrame ? _resultFrames : _controlFrames;

        var slotAcquired = false;
        OutboundFrame? pending = null;

        try
        {
            await AcquireSlotAsync(slots, cancellationToken).ConfigureAwait(false);
            slotAcquired = true;

            if (cancellationToken.IsCancellationRequested || IsStopped)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new ObjectDisposedException(nameof(OutboundQueue), "出站调度队列已停止。");
            }

            var bytes = encode();

            lock (_pendingWriteLock)
            {
                if (_writesStopped != 0)
                {
                    throw new ObjectDisposedException(nameof(OutboundQueue), "出站调度队列已停止。");
                }

                pending = new OutboundFrame(bytes, slots, isResultFrame, OnFrameCompleted, shouldWrite, onWriting, onSent);
                RegisterPendingWriteLocked();
                slotAcquired = false;
                _outstandingFrames.TryAdd(pending, 0);

                if (!channel.Writer.TryWrite(pending))
                {
                    _outstandingFrames.TryRemove(pending, out _);
                    pending.Completion.TrySetException(new ObjectDisposedException(nameof(OutboundQueue), "出站调度队列已停止。"));
                    pending.ReleaseSlot();
                    throw new ObjectDisposedException(nameof(OutboundQueue), "出站调度队列已停止。");
                }

                TryReleaseWriteSignal();
            }

            using var cancellationReg = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(static state =>
                {
                    var (f, token) = ((OutboundFrame, CancellationToken))state!;
                    f.Abandon(token);
                }, (pending, cancellationToken))
                : default;

            await pending.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            if (slotAcquired)
            {
                slots.Release();
            }

            if (pending is not null)
            {
                _outstandingFrames.TryRemove(pending, out _);
            }

            lock (_pendingWriteLock)
            {
                _activeProducers--;
                TryCompleteUsersExitedLocked();
            }
        }
    }

    /// <summary>
    /// 有限等待所有已入队的待写帧完全写出并刷新至底层流。
    /// </summary>
    /// <param name="timeout">最大等待超时时限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task CompleteWritesAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task waitTask;
        lock (_pendingWriteLock)
        {
            if (_pendingFrameCount == 0)
            {
                return;
            }

            waitTask = _writesIdle.Task;
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
        await waitTask.WaitAsync(linked.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 运行单一写循环主逻辑，负责将出站信封串行写入底层数据流。
    /// 调度策略：批量处理最多 ControlBatchLimit 条控制帧后，优先检查并写出 1 条结果帧，防止连续控制帧导致流结果饥饿。
    /// </summary>
    /// <param name="stream">底层可写的双工数据流。</param>
    /// <param name="loopCancellationToken">写循环运行的取消令牌。</param>
    public async Task RunWriteLoopAsync(Stream stream, CancellationToken loopCancellationToken)
    {
        RegisterWriteLoop();

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                _shutdownCts.Token,
                loopCancellationToken);
            var token = linkedCts.Token;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _writeSignal.WaitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                // 批次处理控制帧（上限 ControlBatchLimit 条）
                var controlCount = 0;
                while (controlCount < ControlBatchLimit && _controlFrames.Reader.TryRead(out var control))
                {
                    await WritePendingFrameAsync(stream, control, token).ConfigureAwait(false);
                    controlCount++;
                }

                // 写出一个结果帧，防止结果流饥饿，严格保持流元素顺序
                if (_resultFrames.Reader.TryRead(out var result))
                {
                    await WritePendingFrameAsync(stream, result, token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Stop(new ObjectDisposedException(nameof(OutboundQueue), "写循环已终止。"));
            try
            {
                EnsureDrained();
            }
            finally
            {
                // try 内的局部取消源已释放，写循环不再持有任何同步对象。
                _writeLoopExited.TrySetResult();
            }
        }
    }

    /// <summary>
    /// 停止出站队列，通知所有等待者并中止后续写操作。
    /// 在同一次同步裁决中确立停止状态和唯一取消工作所有权；重复调用观察同一个真实完成信号。
    /// 外部取消回调在锁外异步执行，禁止在释放竞态中吞掉 ObjectDisposedException 掩盖缺陷。
    /// </summary>
    /// <param name="failure">引发停止的异常原因。</param>
    public void Stop(Exception failure)
    {
        bool drainWithoutWriter;
        lock (_pendingWriteLock)
        {
            if (_writesStopped != 0)
            {
                return;
            }
            _writesStopped = 1;
            drainWithoutWriter = _writeLoopScheduled == 0;

            // 优先在锁内完成通道写端并使所有待写帧收到失败通知，不依赖可能阻塞的外部取消回调
            _controlFrames.Writer.TryComplete();
            _resultFrames.Writer.TryComplete();

            foreach (var frame in _outstandingFrames.Keys)
            {
                frame.Completion.TrySetException(failure);
            }

            _writesIdle.TrySetResult();
            TryCompleteUsersExitedLocked();
        }

        // 首个停止方持有取消工作；重复调用始终观察预先创建的真实完成信号。
        _ = Task.Run(() =>
        {
            try
            {
                _shutdownCts.Cancel();
                _shutdownTcs.TrySetResult();
            }
            catch (Exception ex)
            {
                _shutdownTcs.TrySetException(ex);
            }
        });

        if (drainWithoutWriter)
        {
            try
            {
                EnsureDrained();
            }
            finally
            {
                _writeLoopExited.TrySetResult();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _ = GetOrCreateDisposeTask();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return new ValueTask(GetOrCreateDisposeTask());
    }

    /// <summary>
    /// 获取或创建共享的幂等释放协调任务。
    /// </summary>
    /// <returns>全局唯一的队列释放任务。</returns>
    private Task GetOrCreateDisposeTask()
    {
        lock (_disposeLock)
        {
            return _disposeTask ??= PerformDisposeCoreAsync();
        }
    }

    /// <summary>
    /// 停止接纳并在统一的两秒预算内等待释放；实际释放任务一直持有至全部使用者退出。
    /// </summary>
    /// <returns>异步释放任务。</returns>
    private async Task PerformDisposeCoreAsync()
    {
        Volatile.Write(ref _disposed, 1);
        Stop(new ObjectDisposedException(nameof(OutboundQueue), "出站调度队列已释放。"));

        _primitiveDisposalTask = DisposePrimitivesAfterExitAsync();
        await WaitWithTimeoutObservedAsync(_primitiveDisposalTask, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    /// <summary>
    /// 在指定的有限超时预算内等待目标任务，显式处理超时并观察前置任务的异常。
    /// </summary>
    /// <param name="task">待等待的目标异步任务。</param>
    /// <param name="timeout">有限超时时间间隔。</param>
    /// <returns>在超时或前置任务完成后返回的异步等待任务。</returns>
    private static async Task WaitWithTimeoutObservedAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 超时属于受控降级行为：调用方安全结束前台等待，后台继续持有并观察前置任务直至退出
            _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception)
        {
            // 前置任务自身抛出的异常在有限等待内已被观察，避免未观察任务异常
        }
    }

    /// <summary>
    /// 等待实际退出后释放同步原语，取消工作异常由退出任务保留供连接汇聚记录。
    /// </summary>
    /// <returns>同步原语实际释放结束的任务。</returns>
    private async Task DisposePrimitivesAfterExitAsync()
    {
        try
        {
            await _exitTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 汇聚任务故障也意味着全部使用者已退出，可释放本地同步对象。
        }
        finally
        {
            DisposePrimitives();
        }
    }

    /// <summary>
    /// 安全释放底层信号量与取消源。
    /// </summary>
    private void DisposePrimitives()
    {
        lock (_disposeLock)
        {
            if (_primitivesDisposed)
            {
                return;
            }

            _primitivesDisposed = true;
            try { _controlSlots.Dispose(); } catch { }
            try { _resultSlots.Dispose(); } catch { }
            try { _writeSignal.Dispose(); } catch { }
            try { _shutdownCts.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 在状态锁内确认停止后全部生产者和待写帧已退出。停止后不再接纳新使用者，归零信号不可逆。
    /// </summary>
    private void TryCompleteUsersExitedLocked()
    {
        if (_writesStopped != 0 && _activeProducers == 0 && _pendingFrameCount == 0)
        {
            _usersExited.TrySetResult();
        }
    }

    /// <summary>
    /// 请求分配指定信号量的队列名额。
    /// </summary>
    private async Task AcquireSlotAsync(SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        if (slots.Wait(0))
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdownCts.Token,
            cancellationToken);

        try
        {
            await slots.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new ObjectDisposedException(nameof(OutboundQueue), "队列在等待出站名额时已停止。");
        }
    }

    /// <summary>
    /// 将单条出站帧实际写出至底层数据流并刷新。
    /// </summary>
    private async Task WritePendingFrameAsync(Stream stream, OutboundFrame pending, CancellationToken cancellationToken)
    {
        try
        {
            if (pending.TryBeginWrite())
            {
                await stream.WriteAsync(pending.FrameBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                pending.NotifySent();
                pending.Completion.TrySetResult();
            }
            else
            {
                // 跳过写出（例如写入前已取消或中止）：必须完成 Completion，唤醒等待该帧写出完成的调用方
                pending.Completion.TrySetCanceled(cancellationToken.IsCancellationRequested ? cancellationToken : CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            pending.Completion.TrySetException(ex);
            Stop(ex);
            throw;
        }
        finally
        {
            pending.ReleaseSlot();
        }
    }

    /// <summary>
    /// 在锁内登记新的待写帧并重置空闲信号。
    /// </summary>
    private void RegisterPendingWriteLocked()
    {
        if (_pendingFrameCount == 0)
        {
            _writesIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _pendingFrameCount++;
    }

    /// <summary>
    /// 当已入队的帧写出完毕或丢弃时触发，更新未决待写帧数并在全部写出时发出空闲信号。
    /// </summary>
    private void OnFrameCompleted()
    {
        lock (_pendingWriteLock)
        {
            if (--_pendingFrameCount == 0)
            {
                _writesIdle.TrySetResult();
            }
            TryCompleteUsersExitedLocked();
        }
    }

    /// <summary>
    /// 尝试触发写循环就绪信号量。
    /// </summary>
    private void TryReleaseWriteSignal()
    {
        try
        {
            _writeSignal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// 原子保证通道中的残留待写帧仅被排空并释放名额一次。
    /// </summary>
    private void EnsureDrained()
    {
        if (Interlocked.Exchange(ref _drained, 1) != 0)
        {
            return;
        }

        DrainAndReleaseChannel(_controlFrames);
        DrainAndReleaseChannel(_resultFrames);
    }

    /// <summary>
    /// 停止时排空通道中的残留待写帧并释放其持有的名额。
    /// </summary>
    private static void DrainAndReleaseChannel(Channel<OutboundFrame> channel)
    {
        while (channel.Reader.TryRead(out var frame))
        {
            frame.Completion.TrySetException(new ObjectDisposedException(nameof(OutboundQueue), "写循环已终止，丢弃未写出的帧。"));
            frame.ReleaseSlot();
        }
    }

    /// <summary>
    /// 若队列已停止或释放则抛出 ObjectDisposedException。
    /// </summary>
    private void ThrowIfStopped()
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _writesStopped) != 0)
        {
            throw new ObjectDisposedException(nameof(OutboundQueue), "出站调度队列已停止或释放。");
        }
    }
}
