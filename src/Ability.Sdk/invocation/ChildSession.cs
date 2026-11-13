using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Threading.Channels;
using Ability.Core;

namespace Ability.Sdk;

/// <summary>
/// 单个子 Launcher 进程的 IPC 通信会话，持有子管道、入站结果队列、流状态机与控制转发状态。
/// </summary>
internal sealed class ChildSession : IAsyncDisposable
{
    /// <summary>
    /// 诊断记录使用的类别。
    /// </summary>
    private const string DiagnosticCategory = "Ability.Sdk.ChildSession";

    /// <summary>
    /// 默认控制消息出站写出超时时限（2 秒）。
    /// </summary>
    private static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 所属父级会话实例。
    /// </summary>
    private readonly AbilitySession _parentSession;

    /// <summary>
    /// 结果帧缓冲队列（容量为 4）。
    /// </summary>
    private readonly Channel<IpcEnvelope> _data = Channel.CreateBounded<IpcEnvelope>(
        new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

    /// <summary>
    /// 子管道写入串行互斥门控。
    /// </summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>
    /// 流状态与信用额度变更同步锁。
    /// </summary>
    private readonly object _streamStateLock = new();

    /// <summary>
    /// 控制消息顺序转发通道，确保入站控制通知按先后到达顺序逐条转给宿主。
    /// </summary>
    private readonly Channel<IpcEnvelope> _forwardChannel = Channel.CreateBounded<IpcEnvelope>(
        new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

    /// <summary>
    /// 控制请求类消息并发中继门控，限制后台待决审批任务并发数（容量 32）。
    /// </summary>
    private readonly SemaphoreSlim _requestGate = new(32, 32);

    /// <summary>
    /// 请求门控获取与关闭状态同步互斥锁。
    /// </summary>
    private readonly object _requestGateLock = new();

    /// <summary>
    /// 门控与请求接收是否已关闭的标志位。
    /// </summary>
    private bool _closed;

    /// <summary>
    /// 释放流程共享单例任务。
    /// </summary>
    private Task? _disposeTask;

    /// <summary>
    /// 释放互斥同步锁。
    /// </summary>
    private readonly object _disposeLock = new();

    /// <summary>
    /// 跟踪在途后台中继请求任务集合。
    /// </summary>
    private readonly ConcurrentDictionary<Task, byte> _pendingRequests = new();

    /// <summary>
    /// 读循环与转发的停止取消源。
    /// </summary>
    private readonly CancellationTokenSource _readerStop = new();

    /// <summary>
    /// 回收操作单次执行互斥锁。
    /// </summary>
    private readonly object _cleanupLock = new();

    /// <summary>
    /// 当前在途停止尝试或已确认成功的进程回收任务。
    /// </summary>
    private Task<ChildReapedBody>? _cleanupTask;

    /// <summary>
    /// 管道读取循环异步任务。
    /// </summary>
    private Task? _readerTask;

    /// <summary>
    /// 控制消息后台转发循环任务。
    /// </summary>
    private Task? _forwardLoopTask;

    /// <summary>
    /// 写出是否已损坏的标志位。
    /// </summary>
    private int _writerFailed;

    /// <summary>
    /// 当前剩余的流式信用额度。
    /// </summary>
    private int _creditCount;

    /// <summary>
    /// 结果流是否已打开。
    /// </summary>
    private bool _streamOpened;

    /// <summary>
    /// 结果流是否已关闭。
    /// </summary>
    private bool _streamClosed;

    /// <summary>
    /// 是否已接收一元终态帧。
    /// </summary>
    private bool _unaryTerminal;

    /// <summary>
    /// 是否已发出启动请求标记。
    /// </summary>
    private int _startRequested;

    /// <summary>
    /// 是否已确认启动成功标记。
    /// </summary>
    private int _started;

    /// <summary>
    /// 是否已成功确认子进程回收或确认未启动。
    /// </summary>
    private int _reapedConfirmed;

    /// <summary>
    /// 初始化子 Launcher 会话实例。
    /// </summary>
    /// <param name="parentSession">父级技能会话。</param>
    /// <param name="invocationKey">本次子调用的唯一调用键。</param>
    /// <param name="childPipe">建立的子命名管道服务端流。</param>
    /// <param name="initialCreditWindow">初始流式额度窗口大小，必须大于 0。</param>
    public ChildSession(
        AbilitySession parentSession,
        string invocationKey,
        NamedPipeServerStream childPipe,
        int initialCreditWindow = 4)
    {
        if (initialCreditWindow <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCreditWindow), "初始额度窗口必须大于 0。");
        }

        _parentSession = parentSession ?? throw new ArgumentNullException(nameof(parentSession));
        InvocationKey = invocationKey ?? throw new ArgumentNullException(nameof(invocationKey));
        Pipe = childPipe ?? throw new ArgumentNullException(nameof(childPipe));
        _creditCount = initialCreditWindow;
    }

    /// <summary>
    /// 获取本次子调用的唯一调用键。
    /// </summary>
    public string InvocationKey { get; }

    /// <summary>
    /// 获取子管道流实例。
    /// </summary>
    public NamedPipeServerStream Pipe { get; }

    /// <summary>
    /// 获取是否已向宿主发起 ChildStart 启动请求。
    /// </summary>
    public bool StartRequested => Volatile.Read(ref _startRequested) != 0;

    /// <summary>
    /// 获取结果流是否已被子进程正常关闭。
    /// </summary>
    public bool ResultStreamClosed
    {
        get
        {
            lock (_streamStateLock)
            {
                return _streamClosed;
            }
        }
    }

    /// <summary>
    /// 标记已发出启动请求，此后任何退出路径均须协调回收。
    /// </summary>
    public void MarkStartRequested() => Volatile.Write(ref _startRequested, 1);

    /// <summary>
    /// 标记子进程已成功启动。
    /// </summary>
    public void MarkStarted() => Volatile.Write(ref _started, 1);

    /// <summary>
    /// 启动子管道入站读取分发循环及后台顺序控制转发循环。
    /// </summary>
    public void StartReader()
    {
        _forwardLoopTask = ForwardLoopAsync(_readerStop.Token);
        _readerTask = ReadLoopAsync();
    }

    /// <summary>
    /// 按序读取来自子调用的入站结果或终态信封。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>读取到的 IPC 信封。</returns>
    public async ValueTask<IpcEnvelope> ReadDataAsync(CancellationToken cancellationToken)
        => await _data.Reader.ReadAsync(cancellationToken);

    /// <summary>
    /// 向子管道原子写出一个 IPC 信封。
    /// </summary>
    /// <param name="message">待写出的信封。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步写出任务。</returns>
    public async Task WriteAsync(IpcEnvelope message, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _writerFailed) != 0)
            {
                throw new IOException("子调用管道写出已失败，连接不可继续发送。");
            }

            if (message.Type == ProtocolConstants.MessageTypes.ResultCredit)
            {
                var credit = ProtocolMessages.ReadBody<ResultCreditBody>(message);
                lock (_streamStateLock)
                {
                    if (_streamClosed)
                    {
                        return;
                    }

                    if (!_streamOpened || credit.Count <= 0)
                    {
                        throw new ProtocolException("子调用在无效流状态下接收到 ResultCredit。");
                    }

                    _creditCount = checked(_creditCount + credit.Count);
                }
            }

            try
            {
                await IpcFrame.WriteAsync(Pipe, message, cancellationToken);
            }
            catch
            {
                Interlocked.Exchange(ref _writerFailed, 1);
                _readerStop.Cancel();
                throw;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 使用读循环取消令牌向子管道写出控制信封，支持调用方取消。
    /// </summary>
    /// <param name="message">待写出的控制信封。</param>
    /// <param name="cancellationToken">附加取消令牌。</param>
    /// <returns>异步写出任务。</returns>
    public async Task SendControlAsync(IpcEnvelope message, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_readerStop.Token, cancellationToken);
        await WriteAsync(message, linked.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 请求回收子进程并等待宿主确认（幂等操作）。
    /// </summary>
    /// <param name="reason">停止原因。</param>
    /// <returns>宿主回发的回收确认体。</returns>
    public Task<ChildReapedBody> StopAndConfirmAsync(string reason)
    {
        TaskCompletionSource<ChildReapedBody> completion;
        lock (_cleanupLock)
        {
            if (_cleanupTask is { } current
                && (!current.IsCompleted || current.IsCompletedSuccessfully))
            {
                return current;
            }

            completion = new TaskCompletionSource<ChildReapedBody>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _cleanupTask = completion.Task;
        }

        var attemptTask = CompleteStopAttemptAsync(reason, completion);
        ObserveStopTask(attemptTask);
        ObserveStopTask(completion.Task);
        return completion.Task;
    }

    /// <summary>
    /// 执行已发布的停止尝试，并在失败时仅清除仍由本尝试持有的缓存任务。
    /// </summary>
    /// <param name="reason">停止原因。</param>
    /// <param name="completion">在启动实际 I/O 前已发布给并发调用方的完成源。</param>
    /// <returns>观察单次停止尝试结果的后台任务。</returns>
    private async Task CompleteStopAttemptAsync(
        string reason,
        TaskCompletionSource<ChildReapedBody> completion)
    {
        try
        {
            var reaped = await StopCoreAsync(reason).ConfigureAwait(false);
            completion.TrySetResult(reaped);
        }
        catch (Exception exception)
        {
            lock (_cleanupLock)
            {
                if (ReferenceEquals(_cleanupTask, completion.Task))
                {
                    _cleanupTask = null;
                }

                completion.TrySetException(exception);
            }
        }
    }

    /// <summary>
    /// 观察辅助停止任务及共享结果任务的迟到异常，不改变等待者收到的结果。
    /// </summary>
    /// <param name="task">需要观察异常的任务。</param>
    private static void ObserveStopTask(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// 核心回收流程：尽力向子进程写出取消信号，向宿主请求回收并等待 ChildReaped 确认。
    /// </summary>
    /// <param name="reason">停止原因。</param>
    /// <returns>宿主回收确认体。</returns>
    private async Task<ChildReapedBody> StopCoreAsync(string reason)
    {
        try
        {
            return await StopAndConfirmCoreAsync(reason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _parentSession.ChildRouter.RecordStopFailure(InvocationKey, ex);
            throw;
        }
    }

    /// <summary>
    /// 执行一次停止请求并核验宿主对回收结果的确认。
    /// </summary>
    /// <param name="reason">停止原因。</param>
    /// <returns>宿主确认的回收结果。</returns>
    private async Task<ChildReapedBody> StopAndConfirmCoreAsync(string reason)
    {
        if (Pipe.IsConnected && !string.Equals(reason, "Completed", StringComparison.Ordinal))
        {
            using var sendLimit = new CancellationTokenSource(_parentSession.CleanupGracePeriod);
            try
            {
                await WriteAsync(ProtocolMessages.CreateCancel(), sendLimit.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                AbilityDiagnostics.Log("Warning", DiagnosticCategory, 0, $"子调用 Cancel 未能写出：{AbilityDiagnostics.ExceptionSummary(ex)}");
            }
        }

        using var cleanupLimit = new CancellationTokenSource(_parentSession.CleanupGracePeriod + TimeSpan.FromSeconds(5));
        var response = await _parentSession.RequestAsync(
            ProtocolMessages.CreateChildStop(
                _parentSession.NextRequestId(),
                InvocationKey,
                _parentSession.InvocationKey,
                reason),
            cleanupLimit.Token);

        if (response.Type != ProtocolConstants.MessageTypes.ChildReaped)
        {
            var fault = ProtocolMessages.ReadBody<RequestFaultBody>(response);
            if (Volatile.Read(ref _started) == 0 && fault.Kind is "UnknownChild" or "ChildStartFailed")
            {
                Volatile.Write(ref _reapedConfirmed, 1);
                _parentSession.ChildRouter.RemoveChildSession(InvocationKey);
                return new ChildReapedBody(InvocationKey, null, 0, "NotStarted");
            }

            throw new AbilityRequestFaultException(fault.Kind, fault.Message, "ChildSession");
        }

        var reaped = ProtocolMessages.ReadBody<ChildReapedBody>(response);
        if (!string.Equals(reaped.InvocationKey, InvocationKey, StringComparison.Ordinal)
            || reaped.Outcome != "Reaped")
        {
            throw new ProtocolException("宿主未确认子调用进程已回收。");
        }

        Volatile.Write(ref _reapedConfirmed, 1);
        _parentSession.ChildRouter.RemoveChildSession(InvocationKey);
        return reaped;
    }

    /// <summary>
    /// 核验入站结果帧，并在同一锁内更新本端流状态与剩余额度。
    /// </summary>
    /// <param name="message">入站结果信封。</param>
    private void ValidateIncomingData(IpcEnvelope message)
    {
        lock (_streamStateLock)
        {
            switch (message.Type)
            {
                case ProtocolConstants.MessageTypes.ResultOpen:
                    if (_streamOpened || _streamClosed || _unaryTerminal)
                    {
                        throw new ProtocolException("子调用重复打开或在一元终态后打开结果流。");
                    }

                    _streamOpened = true;
                    break;

                case ProtocolConstants.MessageTypes.ResultItem:
                    if (!_streamOpened || _streamClosed || _unaryTerminal || _creditCount <= 0)
                    {
                        throw new ProtocolException("子调用在未打开流或没有额度时发送 ResultItem。");
                    }

                    _creditCount--;
                    break;

                case ProtocolConstants.MessageTypes.ResultClose:
                    if (!_streamOpened || _streamClosed || _unaryTerminal)
                    {
                        throw new ProtocolException("子调用在无效流状态下发送 ResultClose。");
                    }

                    _streamClosed = true;
                    break;

                case ProtocolConstants.MessageTypes.SubmitResult:
                    if (_streamOpened || _streamClosed || _unaryTerminal)
                    {
                        throw new ProtocolException("子调用在流式结果期间提交一元终态。");
                    }

                    _unaryTerminal = true;
                    break;

                case ProtocolConstants.MessageTypes.SubmitFault:
                    if (_streamClosed || _unaryTerminal)
                    {
                        throw new ProtocolException("子调用在终态后重复提交故障。");
                    }

                    // 故障可以结束已打开的流；终态标记阻止后续元素和关闭帧。
                    _unaryTerminal = true;
                    break;
            }
        }
    }

    /// <summary>
    /// 子管道入站读取循环，将消息区分为控制转发与结果数据两类分别派发。
    /// </summary>
    private async Task ReadLoopAsync()
    {
        using var forwardingStop = CancellationTokenSource.CreateLinkedTokenSource(_readerStop.Token);
        try
        {
            while (!forwardingStop.IsCancellationRequested)
            {
                var message = await IpcFrame.ReadAsync(Pipe, forwardingStop.Token);
                if (message is null)
                {
                    throw new AbilityDisconnectedException("子 Launcher 已关闭 IPC 管道。");
                }

                if (IsForwardedControl(message))
                {
                    await _forwardChannel.Writer.WriteAsync(message, forwardingStop.Token).ConfigureAwait(false);
                    continue;
                }

                ValidateIncomingData(message);
                await _data.Writer.WriteAsync(message, forwardingStop.Token);

                if (message.Type is ProtocolConstants.MessageTypes.SubmitResult
                    or ProtocolConstants.MessageTypes.SubmitFault
                    or ProtocolConstants.MessageTypes.ResultClose)
                {
                    _data.Writer.TryComplete();
                }
            }
        }
        catch (OperationCanceledException) when (forwardingStop.IsCancellationRequested)
        {
            _data.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _data.Writer.TryComplete(ex);
        }
        finally
        {
            _forwardChannel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// 单一后台控制转发循环，按入队先后顺序处理中继至宿主，请求、答复与通知严格分离。
    /// 通知类消息与答复类消息直接在循环内按序中继，保证状态与进度绝不乱序且不占用审批门控；
    /// 请求类消息受有界待决请求池门控（32）限制，满载时立即有界拒绝，防止长审批挂死循环阻塞后续通知。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task ForwardLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _forwardChannel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_forwardChannel.Reader.TryRead(out var message))
                {
                    if (IsBidirectionalRequest(message, out var innermost))
                    {
                        Task? requestTask = null;
                        lock (_requestGateLock)
                        {
                            if (!_closed && _requestGate.Wait(0))
                            {
                                requestTask = DispatchRequestAsync(message, cancellationToken);
                                _pendingRequests.TryAdd(requestTask, 0);
                            }
                        }

                        if (requestTask is not null)
                        {
                            _ = requestTask.ContinueWith(
                                static (task, state) =>
                                {
                                    _ = task.Exception;
                                    ((ConcurrentDictionary<Task, byte>)state!).TryRemove(task, out _);
                                },
                                _pendingRequests,
                                CancellationToken.None,
                                TaskContinuationOptions.ExecuteSynchronously,
                                TaskScheduler.Default);
                        }
                        else
                        {
                            await RejectOverloadedRequestAsync(message, innermost, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await RelayControlMessageAsync(message, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _data.Writer.TryComplete(ex);
            _readerStop.Cancel();
        }
    }

    /// <summary>
    /// 判断消息是否属于需要占用并发门控并等待宿主答复的双向控制请求。
    /// 包含治理申请（LeaseRequest、ParamRequest、SourceRepair、OperationRegister）以及嵌套子调用控制（ChildStart、ChildStop）。
    /// </summary>
    /// <param name="envelope">待检查信封。</param>
    /// <param name="innermost">拆解后的最内层信封。</param>
    /// <returns>若为双向控制请求返回 true，否则返回 false。</returns>
    private static bool IsBidirectionalRequest(IpcEnvelope envelope, out IpcEnvelope innermost)
    {
        innermost = envelope;
        while (string.Equals(innermost.Type, ProtocolConstants.MessageTypes.ChildMessage, StringComparison.Ordinal))
        {
            var wrapper = ProtocolMessages.ReadBody<ChildMessageBody>(innermost);
            if (wrapper.Message is null)
            {
                return false;
            }
            innermost = wrapper.Message;
        }

        return innermost.Id is not null
            && (string.Equals(innermost.Type, ProtocolConstants.MessageTypes.LeaseRequest, StringComparison.Ordinal)
                || string.Equals(innermost.Type, ProtocolConstants.MessageTypes.ParamRequest, StringComparison.Ordinal)
                || string.Equals(innermost.Type, ProtocolConstants.MessageTypes.SourceRepair, StringComparison.Ordinal)
                || string.Equals(innermost.Type, ProtocolConstants.MessageTypes.OperationRegister, StringComparison.Ordinal)
                || string.Equals(innermost.Type, ProtocolConstants.MessageTypes.ChildStart, StringComparison.Ordinal)
                || string.Equals(innermost.Type, ProtocolConstants.MessageTypes.ChildStop, StringComparison.Ordinal));
    }

    /// <summary>
    /// 在待决请求池满载时，向子进程回发明确的 RequestFault 答复，避免请求挂死或阻塞主循环。
    /// 使用独立发送时限（2 秒），若交付失败则显式终结子会话以防挂死。
    /// </summary>
    /// <param name="message">原始转发消息。</param>
    /// <param name="innermost">最内层请求消息。</param>
    /// <param name="cancellationToken">会话取消令牌。</param>
    private async Task RejectOverloadedRequestAsync(IpcEnvelope message, IpcEnvelope innermost, CancellationToken cancellationToken)
    {
        AbilityDiagnostics.Log("Warning", DiagnosticCategory, 0, $"子调用待决请求池满载，拒绝调度新请求：{innermost.Type} (Id={innermost.Id})");
        var fault = ProtocolMessages.CreateRequestFault(
            innermost.Id ?? 0,
            "ServerBusy",
            "子调用待决请求池满载，拒绝调度新请求。");

        var response = WrapInnerReply(message, fault);
        try
        {
            using var sendCts = new CancellationTokenSource(DefaultSendTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(sendCts.Token, cancellationToken);
            await WriteAsync(response, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AbilityDiagnostics.Log("Warning", DiagnosticCategory, 0, $"回发待决请求池满载答复失败，终结子会话：{AbilityDiagnostics.ExceptionSummary(ex)}");
            _data.Writer.TryComplete(ex);
            _readerStop.Cancel();
        }
    }

    /// <summary>
    /// 递归将最内层答复包装回对应的 ChildMessageReply 嵌套结构。
    /// </summary>
    /// <param name="original">原始包装消息。</param>
    /// <param name="reply">最内层答复信封。</param>
    /// <returns>匹配原始层级的答复信封。</returns>
    private static IpcEnvelope WrapInnerReply(IpcEnvelope original, IpcEnvelope reply)
    {
        if (!string.Equals(original.Type, ProtocolConstants.MessageTypes.ChildMessage, StringComparison.Ordinal))
        {
            return reply;
        }

        var wrapper = ProtocolMessages.ReadBody<ChildMessageBody>(original);
        var innerReply = wrapper.Message is not null
            ? WrapInnerReply(wrapper.Message, reply)
            : reply;

        return ProtocolMessages.CreateChildMessageReply(original.Id ?? 0, wrapper.InvocationKey, innerReply);
    }

    /// <summary>
    /// 异步处理待决控制请求的中继与答复写回，完成后释放并发名额。
    /// </summary>
    /// <param name="message">内层待转发请求消息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task DispatchRequestAsync(IpcEnvelope message, CancellationToken cancellationToken)
    {
        try
        {
            await RelayControlMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AbilityDiagnostics.Log("Warning", DiagnosticCategory, 0, $"子调用请求转发失败：{AbilityDiagnostics.ExceptionSummary(ex)}");
            _data.Writer.TryComplete(ex);
            _readerStop.Cancel();
        }
        finally
        {
            _requestGate.Release();
        }
    }

    /// <summary>
    /// 将子调用的控制消息中继至宿主，并将可选答复写回子管道。
    /// </summary>
    /// <param name="message">内层待转发消息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task RelayControlMessageAsync(IpcEnvelope message, CancellationToken cancellationToken)
    {
        try
        {
            RegisterDescendantRoutes(message);

            var requestId = _parentSession.NextRequestId();
            var relayed = await _parentSession.RequestAsync(
                ProtocolMessages.CreateChildMessage(requestId, InvocationKey, message),
                cancellationToken).ConfigureAwait(false);

            if (relayed.Id != requestId)
            {
                throw new ProtocolException("ChildMessageReply 的请求编号与当前 ChildMessage 不匹配。");
            }

            var reply = ProtocolMessages.ReadBody<ChildMessageReplyBody>(relayed);
            if (!string.Equals(reply.InvocationKey, InvocationKey, StringComparison.Ordinal))
            {
                throw new ProtocolException("ChildMessageReply 的调用键与当前子调用不匹配。");
            }

            RemoveStoppedRoute(message, reply.Response);

            if (reply.Response is IpcEnvelope response)
            {
                await WriteAsync(response, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _data.Writer.TryComplete(ex);
            _readerStop.Cancel();
            throw;
        }
    }

    /// <summary>
    /// 递归解析子消息以登记后代路由映射。
    /// </summary>
    /// <param name="message">待检查的消息。</param>
    private void RegisterDescendantRoutes(IpcEnvelope message)
    {
        var nested = message;
        while (nested.Type == ProtocolConstants.MessageTypes.ChildMessage)
        {
            var wrapper = ProtocolMessages.ReadBody<ChildMessageBody>(nested);
            if (!string.Equals(wrapper.InvocationKey, InvocationKey, StringComparison.Ordinal))
            {
                _parentSession.ChildRouter.RegisterDescendantRoute(wrapper.InvocationKey, InvocationKey);
            }
            nested = wrapper.Message;
        }

        if (nested.Type == ProtocolConstants.MessageTypes.ChildStart)
        {
            var start = ProtocolMessages.ReadBody<ChildStartBody>(nested);
            _parentSession.ChildRouter.RegisterDescendantRoute(start.InvocationKey, InvocationKey);
        }
    }

    /// <summary>
    /// 逐层匹配原始请求与宿主答复，仅在目标后代确认回收后移除路由映射。
    /// </summary>
    /// <param name="message">可能包含 ChildStop 的原始请求。</param>
    /// <param name="response">宿主代理返回的内层答复。</param>
    private void RemoveStoppedRoute(IpcEnvelope message, IpcEnvelope? response)
    {
        var request = message;
        var reply = response;
        while (request.Type == ProtocolConstants.MessageTypes.ChildMessage)
        {
            var requestWrapper = ProtocolMessages.ReadBody<ChildMessageBody>(request);
            if (reply is null)
            {
                return;
            }

            if (request.Id is not long requestId || reply.Id != requestId)
            {
                throw new ProtocolException("子消息代理答复的请求编号与对应 ChildMessage 不匹配。");
            }

            if (reply.Type != ProtocolConstants.MessageTypes.ChildMessageReply)
            {
                return;
            }

            var replyWrapper = ProtocolMessages.ReadBody<ChildMessageReplyBody>(reply);
            if (!string.Equals(requestWrapper.InvocationKey, replyWrapper.InvocationKey, StringComparison.Ordinal))
            {
                throw new ProtocolException("ChildMessageReply 的调用键与对应 ChildMessage 不匹配。");
            }

            request = requestWrapper.Message;
            reply = replyWrapper.Response;
        }

        if (request.Type != ProtocolConstants.MessageTypes.ChildStop || reply is null)
        {
            return;
        }

        if (request.Id is not long stopRequestId || reply.Id != stopRequestId)
        {
            throw new ProtocolException("子调用停止答复的请求编号与对应 ChildStop 不匹配。");
        }

        if (reply.Type != ProtocolConstants.MessageTypes.ChildReaped)
        {
            return;
        }

        var stop = ProtocolMessages.ReadBody<ChildStopBody>(request);
        var reaped = ProtocolMessages.ReadBody<ChildReapedBody>(reply);
        if (!string.Equals(stop.InvocationKey, reaped.InvocationKey, StringComparison.Ordinal))
        {
            throw new ProtocolException("ChildReaped 的调用键与对应 ChildStop 不匹配。");
        }

        if (reaped.Outcome == "Reaped")
        {
            _parentSession.ChildRouter.RemoveDescendantRoute(stop.InvocationKey);
        }
    }

    /// <summary>
    /// 判断指定的信封是否属于需要中继给宿主裁决的控制消息。
    /// </summary>
    /// <param name="message">待检查的信封。</param>
    /// <returns>属于控制中继消息返回 true，否则返回 false。</returns>
    private static bool IsForwardedControl(IpcEnvelope message)
        => message.Type is ProtocolConstants.MessageTypes.LeaseRequest
            or ProtocolConstants.MessageTypes.ParamRequest
            or ProtocolConstants.MessageTypes.SourceRepair
            or ProtocolConstants.MessageTypes.LeaseRelease
            or ProtocolConstants.MessageTypes.OperationRegister
            or ProtocolConstants.MessageTypes.OperationStarted
            or ProtocolConstants.MessageTypes.AttemptEnded
            or ProtocolConstants.MessageTypes.OperationEnd
            or ProtocolConstants.MessageTypes.OperationState
            or ProtocolConstants.MessageTypes.OperationExpired
            or ProtocolConstants.MessageTypes.PolicyApplied
            or ProtocolConstants.MessageTypes.Progress
            or ProtocolConstants.MessageTypes.ChildStart
            or ProtocolConstants.MessageTypes.ChildStop
            or ProtocolConstants.MessageTypes.ChildMessage
            or "OperationUnresponsive"
            or ProtocolConstants.MessageTypes.RequestFault;

    /// <summary>
    /// 异步释放子会话资源，停止读循环与转发，释放管道并撤销路由。
    /// 并发调用此方法共享同一个底层收尾任务。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            _disposeTask ??= PerformDisposeAsync();
        }
        return new ValueTask(_disposeTask);
    }

    /// <summary>
    /// 执行实际的子会话收尾与释放流程。
    /// </summary>
    private async Task PerformDisposeAsync()
    {
        _readerStop.Cancel();
        _forwardChannel.Writer.TryComplete();
        lock (_requestGateLock)
        {
            _closed = true;
        }

        try
        {
            if (_readerTask is not null)
            {
                await _readerTask.WaitAsync(_parentSession.CleanupGracePeriod).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException)
        {
            AbilityDiagnostics.Log("Warning", DiagnosticCategory, 0, $"停止子调用读取循环发生异常：{AbilityDiagnostics.ExceptionSummary(ex)}");
        }

        try
        {
            if (_forwardLoopTask is not null)
            {
                await _forwardLoopTask.WaitAsync(_parentSession.CleanupGracePeriod).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException)
        {
            AbilityDiagnostics.Log("Warning", DiagnosticCategory, 0, $"等待子调用控制转发结束发生异常：{AbilityDiagnostics.ExceptionSummary(ex)}");
        }

        var pending = _pendingRequests.Keys.ToArray();
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(_parentSession.CleanupGracePeriod).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException)
            {
                AbilityDiagnostics.Log("Warning", DiagnosticCategory, 0, $"等待子调用待决请求排空发生异常：{AbilityDiagnostics.ExceptionSummary(ex)}");
            }
        }

        var loopCompleted = _forwardLoopTask is null || _forwardLoopTask.IsCompleted;
        var requestsRemaining = _pendingRequests.Keys.ToArray();

        if (loopCompleted && requestsRemaining.Length == 0)
        {
            _requestGate.Dispose();
        }
        else
        {
            // 有限超时后仍有循环或任务在途，由后台所有者等待其全部物理退出后再释放门控
            var exitTasks = new List<Task>();
            if (_forwardLoopTask is not null) exitTasks.Add(_forwardLoopTask);
            exitTasks.AddRange(requestsRemaining);

            _ = Task.WhenAll(exitTasks).ContinueWith(static (t, state) =>
            {
                _ = t.Exception;
                try { ((SemaphoreSlim)state!).Dispose(); } catch { }
            }, _requestGate, TaskScheduler.Default);
        }

        await Pipe.DisposeAsync().ConfigureAwait(false);
        if (!StartRequested || Volatile.Read(ref _reapedConfirmed) != 0)
        {
            _parentSession.ChildRouter.RemoveChildSession(InvocationKey);
        }
    }
}
