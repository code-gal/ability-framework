namespace Ability.Core;

/// <summary>
/// <see cref="IpcConnection"/> 的后台循环分部：出站写循环包装、入站读循环与单帧处置步骤、迟到答复派发循环。
/// 循环只通过连接字段与统一终止入口协作，不持有独立的生命周期状态。
/// </summary>
public sealed partial class IpcConnection
{
    /// <summary>读循环对单帧的处置结果。</summary>
    private enum FrameDisposition
    {
        /// <summary>本帧未由当前步骤处置，交由下一步骤核验。</summary>
        NextStep,

        /// <summary>本帧已处置，继续读取下一帧。</summary>
        Handled,

        /// <summary>终止原因已裁决，结束读循环。</summary>
        Terminated
    }

    /// <summary>
    /// 后台写循环包装：捕获写循环异常并触发统一连接终止。
    /// </summary>
    private async Task RunWriteLoopWrapperAsync()
    {
        await _startBarrier.Task.ConfigureAwait(false);
        try
        {
            await _outboundQueue.RunWriteLoopAsync(_stream, _connectionCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_connectionCts.IsCancellationRequested || Volatile.Read(ref _terminating) != 0)
        {
            Terminate(IpcTerminationReason.NormalClosed);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _terminating) != 0)
        {
            Terminate(IpcTerminationReason.NormalClosed);
        }
        catch (IOException) when (Volatile.Read(ref _terminating) != 0)
        {
            Terminate(IpcTerminationReason.NormalClosed);
        }
        catch (Exception ex)
        {
            Terminate(IpcTerminationReason.Disconnected, ex);
        }
    }

    /// <summary>
    /// 后台读循环主逻辑：等待启动屏障后逐帧执行「读取—匹配—流核验—投递」处置，
    /// 任一步骤裁决终止即结束循环，循环实际退出时再触发一次统一终止。
    /// </summary>
    /// <param name="loopToken">读循环运行的取消令牌。</param>
    private async Task RunReadLoopAsync(CancellationToken loopToken)
    {
        await _startBarrier.Task.ConfigureAwait(false);
        try
        {
            while (!loopToken.IsCancellationRequested)
            {
                if (await ReadFrameOrTerminateAsync(loopToken).ConfigureAwait(false) is not { } message)
                {
                    break;
                }

                if (DispatchFrame(message) == FrameDisposition.Terminated)
                {
                    break;
                }
            }
        }
        finally
        {
            Terminate(TerminationReason);
        }
    }

    /// <summary>
    /// 按顺序处置一帧入站消息：请求匹配、结果流核验、入站投递。
    /// 已匹配的答复（含迟到答复投递）结束本帧处置，不再进入后续步骤。
    /// </summary>
    /// <param name="message">读取到的入站信封。</param>
    /// <returns>本帧处置结果；仅 <see cref="FrameDisposition.Terminated"/> 要求结束读循环。</returns>
    private FrameDisposition DispatchFrame(IpcEnvelope message)
    {
        var disposition = TryHandleMatchedResponse(message);
        if (disposition != FrameDisposition.NextStep)
        {
            return disposition;
        }

        return ValidateResultFrame(message) == FrameDisposition.Terminated
            ? FrameDisposition.Terminated
            : PublishInboundFrame(message);
    }

    /// <summary>
    /// 读取下一帧，并在读取失败或对端结束时裁决终止原因。
    /// 终止原因裁决集中在此：已终止状态下的取消与流错误归为正常关闭，
    /// 对端 EOF 归为对端停止，协议与载荷错误归为协议损坏，其余错误归为意外断开。
    /// </summary>
    /// <param name="loopToken">读循环运行的取消令牌。</param>
    /// <returns>读取到的信封；任何终止路径均返回 null 表示读循环应当结束。</returns>
    private async Task<IpcEnvelope?> ReadFrameOrTerminateAsync(CancellationToken loopToken)
    {
        try
        {
            var message = await IpcFrame.ReadAsync(_stream, loopToken).ConfigureAwait(false);
            if (message is not null)
            {
                return message;
            }

            // 对端流正常到达 EOF 或管道关闭：记为对端主动停止，不记为传输故障
            Terminate(IpcTerminationReason.PeerStopped);
        }
        catch (OperationCanceledException) when (loopToken.IsCancellationRequested || Volatile.Read(ref _terminating) != 0)
        {
            Terminate(IpcTerminationReason.NormalClosed);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _terminating) != 0)
        {
            Terminate(IpcTerminationReason.NormalClosed);
        }
        catch (IOException) when (Volatile.Read(ref _terminating) != 0)
        {
            Terminate(IpcTerminationReason.NormalClosed);
        }
        catch (ProtocolException ex)
        {
            Terminate(IpcTerminationReason.ProtocolCorrupted, ex);
        }
        catch (PayloadTooLargeException ex)
        {
            Terminate(IpcTerminationReason.ProtocolCorrupted, ex);
        }
        catch (Exception ex)
        {
            Terminate(IpcTerminationReason.Disconnected, ex);
        }

        return null;
    }

    /// <summary>
    /// 交由请求匹配器裁决答复。匹配到迟到答复时仅向专用通道做有界投递，绝不同步执行外部处理者；
    /// 匹配到尚未开始写出的请求答复等协议违规时裁决协议损坏。
    /// </summary>
    /// <param name="message">读取到的入站信封。</param>
    /// <returns>匹配器已处置时返回 Handled，未匹配返回 NextStep，协议违规返回 Terminated。</returns>
    private FrameDisposition TryHandleMatchedResponse(IpcEnvelope message)
    {
        try
        {
            if (!_requestMatcher.TryMatchResponse(message, out var isLateReply))
            {
                return FrameDisposition.NextStep;
            }

            if (isLateReply && !_lateRepliesChannel.Writer.TryWrite(message))
            {
                // 容量耗尽必须明确报告失败，并采用可终止的处理路径
                Terminate(
                    IpcTerminationReason.ProtocolCorrupted,
                    new ProtocolException($"迟到答复通道容量耗尽（上限 {DefaultLateRepliesCapacity}），无法派发并终止连接。"));
                return FrameDisposition.Terminated;
            }

            return FrameDisposition.Handled;
        }
        catch (ProtocolException pe)
        {
            Terminate(IpcTerminationReason.ProtocolCorrupted, pe);
            return FrameDisposition.Terminated;
        }
    }

    /// <summary>
    /// 在底层读循环单点核验结果流协议状态。
    /// 状态转换与计数归本连接的结果流状态对象。
    /// </summary>
    /// <param name="message">读取到的入站信封。</param>
    /// <returns>核验通过返回 NextStep，协议违规返回 Terminated。</returns>
    private FrameDisposition ValidateResultFrame(IpcEnvelope message)
    {
        try
        {
            switch (message.Type)
            {
                case ProtocolConstants.MessageTypes.ResultOpen:
                    _streamState.ValidateAndRecordOpen();
                    return FrameDisposition.NextStep;

                case ProtocolConstants.MessageTypes.ResultItem:
                    _streamState.ValidateAndRecordReceivedItem();
                    return FrameDisposition.NextStep;

                case ProtocolConstants.MessageTypes.ResultClose:
                    _streamState.ValidateAndRecordClose();
                    return FrameDisposition.NextStep;

                case ProtocolConstants.MessageTypes.SubmitResult:
                    // 一元终态只在尚未开流时受理（本端不记录一元终态，按 D16 保持现状）
                    if (_streamState.HasStreamStarted)
                    {
                        throw new ProtocolException("结果流已开启后收到 SubmitResult 属于协议违规。");
                    }

                    return FrameDisposition.NextStep;

                default:
                    return FrameDisposition.NextStep;
            }
        }
        catch (ProtocolException pe)
        {
            Terminate(IpcTerminationReason.ProtocolCorrupted, pe);
            return FrameDisposition.Terminated;
        }
    }

    /// <summary>
    /// 把非答复、非结果协议的信封投递到有界入站通道。
    /// 上层消费严重积压导致通道满时明确拒绝并判定为协议损坏，防止读循环永久受阻。
    /// </summary>
    /// <param name="message">读取到的入站信封。</param>
    /// <returns>投递成功返回 Handled，通道耗尽返回 Terminated。</returns>
    private FrameDisposition PublishInboundFrame(IpcEnvelope message)
    {
        if (_inboundChannel.Writer.TryWrite(message))
        {
            return FrameDisposition.Handled;
        }

        Terminate(
            IpcTerminationReason.ProtocolCorrupted,
            new ProtocolException("入站消息通道容量耗尽，接收循环无法继续派发并终止连接。"));
        return FrameDisposition.Terminated;
    }

    /// <summary>
    /// 后台单消费者迟到答复派发循环：从专用有界通道读取并安全派发给处理者。
    /// </summary>
    /// <param name="loopToken">派发循环运行的取消令牌。</param>
    private async Task RunLateRepliesLoopAsync(CancellationToken loopToken)
    {
        await _startBarrier.Task.ConfigureAwait(false);
        try
        {
            while (await _lateRepliesChannel.Reader.WaitToReadAsync(loopToken).ConfigureAwait(false))
            {
                while (_lateRepliesChannel.Reader.TryRead(out var envelope))
                {
                    try
                    {
                        _lateReplyHandler?.Invoke(envelope);
                    }
                    catch (Exception ex)
                    {
                        // 处理者异常须记录并采用明确的失败处理，保留资源处理责任
                        RecordCleanupFailure(ex);
                        Terminate(IpcTerminationReason.ProtocolCorrupted, new InvalidOperationException($"迟到答复回调处理失败: {ex.GetType().Name}", ex));
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
