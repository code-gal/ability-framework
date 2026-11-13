namespace Ability.Core;

/// <summary>
/// <see cref="IpcConnection"/> 的终止分部：统一、幂等的终止入口与后台收尾步骤。
/// 收尾按固定顺序执行——释放启动屏障、解析终止异常、结束请求与额度等待、
/// 启动取消回调、处置底层流、汇聚实际退出，最后发布终止原因。
/// </summary>
public sealed partial class IpcConnection
{
    /// <summary>
    /// 统一、幂等的连接终止入口。无论是读失败、写失败、EOF 还是主动关闭，均经由此方法收尾。
    /// 生命周期锁内只处理状态及任务引用；取消回调、队列停止、流释放与循环等待全部纳入受管的后台异步收尾任务中。
    /// 读写循环退出时不自我等待，消除未等待异步调用警告并保证有限预算正常收敛。
    /// </summary>
    /// <param name="reason">引发终止的原因。</param>
    /// <param name="failure">引发终止的底层异常。</param>
    private void Terminate(IpcTerminationReason reason, Exception? failure = null)
    {
        SetTerminationReason(reason, failure);
        Volatile.Write(ref _closed, 1);

        lock (_lifecycleLock)
        {
            if (_terminating != 0)
            {
                return;
            }
            _terminating = 1;

            var tasks = new List<Task>();
            if (_readLoopTask is not null) tasks.Add(_readLoopTask);
            if (_writeLoopTask is not null) tasks.Add(_writeLoopTask);
            if (_lateRepliesLoopTask is not null) tasks.Add(_lateRepliesLoopTask);

            // 内部收尾任务由连接持有并在后台安全观察，完整覆盖取消回调、通道队列关闭、流处置与循环退出
            _terminationTask = Task.Run(() => CompleteTerminationAsync(tasks, reason, failure));
        }
    }

    /// <summary>
    /// 后台执行受管终止收尾序列：按固定顺序结束待决工作、启动取消、处置底层流，
    /// 等待已登记循环任务、取消任务与额度等待实际退出，最后完成 _closedTcs。
    /// </summary>
    /// <param name="tasks">需要等待退出的循环任务集合。</param>
    /// <param name="reason">引发终止的原因。</param>
    /// <param name="failure">引发终止的底层异常。</param>
    private async Task CompleteTerminationAsync(List<Task> tasks, IpcTerminationReason reason, Exception? failure)
    {
        try
        {
            // 释放启动屏障，防止未启动完成的后台循环永久挂起
            _startBarrier.TrySetResult();

            var exception = ResolveTerminationFailure(reason, failure);

            // 先使未决请求及已接纳写出任务收到失败通知，不依赖可能阻塞的外部取消回调
            StopPendingWork(failure, exception);

            // 同步取消回调由独立线程池任务执行并受管持有，优先启动以解除读写等待
            var cancelTask = StartCancellationCallback();

            // 流处置位于取消任务启动之后，保证流释放受阻不阻止取消任务解除读写阻塞
            DisposeUnderlyingStream();

            await AwaitActualExitAsync(tasks, cancelTask).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordCleanupFailure(ex);
        }
        finally
        {
            _closedTcs.TrySetResult(TerminationReason);
        }
    }

    /// <summary>
    /// 解析终止处置使用的异常：优先使用引发终止的底层异常，否则按终止分类构造对应异常。
    /// </summary>
    /// <param name="reason">引发终止的原因。</param>
    /// <param name="failure">引发终止的底层异常。</param>
    /// <returns>额度等待者、未决请求与出站队列观察到的终止异常。</returns>
    private static Exception ResolveTerminationFailure(IpcTerminationReason reason, Exception? failure)
    {
        if (failure is not null)
        {
            return failure;
        }

        return reason switch
        {
            IpcTerminationReason.NormalClosed => new ObjectDisposedException(nameof(IpcConnection), "连接已正常关闭。"),
            IpcTerminationReason.CallerCancelled => new OperationCanceledException("连接已被主动取消。"),
            IpcTerminationReason.PeerStopped => new IOException("对端已关闭连接。"),
            IpcTerminationReason.Disconnected => new IOException("底层数据流已意外断开。"),
            IpcTerminationReason.ProtocolCorrupted => new ProtocolException("传输协议或数据损坏。"),
            _ => new IOException("连接已终止。")
        };
    }

    /// <summary>
    /// 结束待决工作：停止结果流额度等待、关闭入站与迟到答复通道、取消全部未决请求并停止出站队列。
    /// 入站通道使用引发终止的原始异常完成（可能为空，此时上层观察到正常结束），
    /// 其余等待者使用解析后的终止异常。
    /// </summary>
    /// <param name="inboundCompletionFailure">入站通道完成时携带的原始终止异常。</param>
    /// <param name="exception">额度等待者与未决请求观察到的连接终止异常。</param>
    private void StopPendingWork(Exception? inboundCompletionFailure, Exception exception)
    {
        try { _streamState.StopCreditWaits(exception); } catch (Exception ex) { RecordCleanupFailure(ex); }
        try { _inboundChannel.Writer.TryComplete(inboundCompletionFailure); } catch (Exception ex) { RecordCleanupFailure(ex); }
        try { _lateRepliesChannel.Writer.TryComplete(); } catch (Exception ex) { RecordCleanupFailure(ex); }
        try { _requestMatcher.CancelAll(exception); } catch (Exception ex) { RecordCleanupFailure(ex); }
        try { _outboundQueue.Stop(exception); } catch (Exception ex) { RecordCleanupFailure(ex); }
    }

    /// <summary>
    /// 在独立线程池任务中触发连接取消，使外部取消回调的异常或阻塞不影响收尾；重复观察到的取消异常记入清理故障。
    /// </summary>
    /// <returns>取消回调实际返回的任务。</returns>
    private Task StartCancellationCallback()
    {
        return Task.Run(() =>
        {
            try
            {
                _connectionCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                RecordCleanupFailure(ex);
            }
        });
    }

    /// <summary>
    /// 处置底层数据流。调用位置固定在取消任务启动之后，保证流释放受阻不阻止取消任务解除读写阻塞。
    /// </summary>
    private void DisposeUnderlyingStream()
    {
        try
        {
            _stream.Dispose();
        }
        catch (Exception ex)
        {
            RecordCleanupFailure(ex);
        }
    }

    /// <summary>
    /// 汇聚等待已登记循环任务、取消任务、出站队列退出信号与额度等待退出，并记录全部清理异常。
    /// 该步骤返回表示内部实际工作已经退出，而不是仅前台等待结束。
    /// </summary>
    /// <param name="tasks">已登记的循环任务集合。</param>
    /// <param name="cancelTask">取消回调任务。</param>
    private async Task AwaitActualExitAsync(List<Task> tasks, Task cancelTask)
    {
        var allTasks = new List<Task>(tasks)
        {
            cancelTask,
            _outboundQueue.ExitTask,
            _streamState.ExitTask
        };
        var completion = Task.WhenAll(allTasks);
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch
        {
            // await 只抛出一个异常；记录汇聚任务中的全部故障，保留原连接终止原因。
            if (completion.Exception is AggregateException aggregate)
            {
                foreach (var cleanupFailure in aggregate.Flatten().InnerExceptions)
                {
                    RecordCleanupFailure(cleanupFailure);
                }
            }
        }
    }
}
