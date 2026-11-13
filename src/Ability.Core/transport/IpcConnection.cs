using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace Ability.Core;

/// <summary>
/// 双工 IPC 连接协调者：接管底层双工 Stream，驱动单一出站写循环与入站接收循环，
/// 统筹请求匹配、结果流状态，并在读写异常、EOF 或主动关闭时执行对称、幂等的统一终止流程。
/// </summary>
public sealed partial class IpcConnection : IAsyncDisposable
{
    /// <summary>默认入站消息有界通道容量。</summary>
    private const int DefaultInboundCapacity = 128;
    /// <summary>默认迟到答复有界通道容量。</summary>
    private const int DefaultLateRepliesCapacity = 64;

    /// <summary>底层双工数据流。</summary>
    private readonly Stream _stream;
    /// <summary>出站写调度队列。</summary>
    private readonly OutboundQueue _outboundQueue;
    /// <summary>并发请求匹配器。</summary>
    private readonly RequestMatcher _requestMatcher = new();
    /// <summary>结果流协议状态管理器。</summary>
    private readonly ResultStreamState _streamState;
    /// <summary>入站消息通道。</summary>
    private readonly Channel<IpcEnvelope> _inboundChannel;
    /// <summary>迟到答复专用通道。</summary>
    private readonly Channel<IpcEnvelope> _lateRepliesChannel;

    /// <summary>连接内部生命周期取消源。</summary>
    private readonly CancellationTokenSource _connectionCts = new();
    /// <summary>连接彻底关闭通知源。</summary>
    private readonly TaskCompletionSource<IpcTerminationReason> _closedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>后台读循环任务。</summary>
    private Task? _readLoopTask;
    /// <summary>后台写循环包装任务。</summary>
    private Task? _writeLoopTask;
    /// <summary>后台迟到答复派发任务。</summary>
    private Task? _lateRepliesLoopTask;

    /// <summary>连接最终终止原因。</summary>
    private int _terminationReason = (int)IpcTerminationReason.NormalClosed;
    /// <summary>是否已置位非正常根本终止原因原子标记。</summary>
    private int _abnormalReasonSet;
    /// <summary>首个根本异常。</summary>
    private Exception? _rootFailure;
    /// <summary>收尾清理期间发生的二次异常。</summary>
    private Exception? _cleanupFailure;

    /// <summary>连接是否已启动标记。</summary>
    private int _started;
    /// <summary>连接是否已关闭标记。</summary>
    private int _closed;
    /// <summary>连接是否正在终止标记。</summary>
    private int _terminating;
    /// <summary>连接资源是否已释放标记。</summary>
    private int _disposed;
    /// <summary>生命周期（启动与终止）同步锁。</summary>
    private readonly object _lifecycleLock = new();
    /// <summary>内部收尾任务引用。</summary>
    private Task? _terminationTask;
    /// <summary>固定的迟到答复回调处理者。</summary>
    private Action<IpcEnvelope>? _lateReplyHandler;

    /// <summary>迟到答复消费模式枚举。</summary>
    private enum LateReplyMode
    {
        /// <summary>尚未选择模式。</summary>
        Unselected = 0,
        /// <summary>回调派发模式。</summary>
        Callback = 1,
        /// <summary>通道消费模式。</summary>
        Channel = 2
    }

    /// <summary>当前选定的迟到答复消费模式。</summary>
    private LateReplyMode _lateReplyMode = LateReplyMode.Unselected;

    /// <summary>后台循环启动屏障，保证循环任务引用完整登记后才允许开始 I/O 与触发终止。</summary>
    private readonly TaskCompletionSource _startBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 初始化 IPC 双工连接。
    /// </summary>
    /// <param name="stream">已建立连接的双工数据流。</param>
    /// <param name="initialCreditWindow">流式任务初始信用额度窗口。</param>
    /// <param name="maxControlFrames">控制队列名额容量（缺省 64）。</param>
    /// <param name="inboundCapacity">入站缓冲通道容量上限（默认 128）。</param>
    public IpcConnection(
        Stream stream,
        int initialCreditWindow = 0,
        int maxControlFrames = 64,
        int inboundCapacity = DefaultInboundCapacity)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _outboundQueue = new OutboundQueue(maxControlFrames, OutboundQueue.MaxPendingResultFrames);
        _streamState = new ResultStreamState(initialCreditWindow);
        var minInboundCapacity = initialCreditWindow + maxControlFrames + 64;
        var effectiveInboundCapacity = Math.Max(inboundCapacity, minInboundCapacity);
        _inboundChannel = Channel.CreateBounded<IpcEnvelope>(new BoundedChannelOptions(effectiveInboundCapacity)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _lateRepliesChannel = Channel.CreateBounded<IpcEnvelope>(new BoundedChannelOptions(DefaultLateRepliesCapacity)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>
    /// 入站消息通道读取端，供上层协议处理逻辑持续消费非等待类的控制消息、通知及业务交付帧。
    /// </summary>
    public ChannelReader<IpcEnvelope> Inbound => _inboundChannel.Reader;

    /// <summary>
    /// 迟到答复通道读取端，供上层治理组件在后台安全回收租约等资源。
    /// 模式选择：首次读取本属性或配置 LateReplyHandler 时固定消费模式；二者互斥且只能在连接启动前决定。
    /// 单消费者责任：外部调用方必须确保本通道仅由单一消费循环读取，杜绝多方争抢。
    /// </summary>
    /// <exception cref="InvalidOperationException">当已选择回调消费模式（配置了 LateReplyHandler）时抛出。</exception>
    public ChannelReader<IpcEnvelope> LateReplies
    {
        get
        {
            lock (_lifecycleLock)
            {
                if (_lateReplyMode == LateReplyMode.Callback)
                {
                    throw new InvalidOperationException("连接已选择回调消费模式（配置了 LateReplyHandler），禁止外部获取迟到答复通道。");
                }

                _lateReplyMode = LateReplyMode.Channel;
                return _lateRepliesChannel.Reader;
            }
        }
    }

    /// <summary>
    /// 结果流协议状态与信用额度管理器。
    /// </summary>
    public ResultStreamState StreamState => _streamState;

    /// <summary>
    /// 请求匹配器，负责单调递增请求编号生成与响应等待协调。
    /// </summary>
    public RequestMatcher RequestMatcher => _requestMatcher;

    /// <summary>
    /// 原子分配本连接单调递增的下一个唯一请求编号（统一委托至请求匹配器）。
    /// </summary>
    /// <returns>分配的请求编号。</returns>
    public long NextRequestId() => _requestMatcher.NextRequestId();

    /// <summary>
    /// 获取连接最终终止原因（严格保留最先发生的首个根本故障原因）。
    /// </summary>
    public IpcTerminationReason TerminationReason => (IpcTerminationReason)Volatile.Read(ref _terminationReason);

    /// <summary>
    /// 首个引发异常终止的根本异常原因（正常关闭时为 null）。
    /// </summary>
    public Exception? RootFailure => _rootFailure;

    /// <summary>
    /// 终止收尾阶段追加发生的次要异常（无后续错误时为 null）。
    /// </summary>
    public Exception? CleanupFailure => Volatile.Read(ref _cleanupFailure);

    /// <summary>
    /// 连接是否已被关闭或正在终止。
    /// </summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>
    /// 迟到答复回调（针对此前已超期或取消的请求）。必须在连接启动前配置，与通道消费互斥。
    /// 模式选择：首次配置非空回调或读取 LateReplies 通道时固定消费模式；启动后处理者固定，不可更改。
    /// 单消费者责任：配置后由连接专属后台循环负责单消费者派发，禁止外部并发消费。
    /// </summary>
    /// <exception cref="ArgumentNullException">当传入 null 时抛出。</exception>
    /// <exception cref="InvalidOperationException">当连接已启动、已关闭或已选择通道消费模式时抛出。</exception>
    public Action<IpcEnvelope>? LateReplyHandler
    {
        get => _lateReplyHandler;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lifecycleLock)
            {
                if (Volatile.Read(ref _started) != 0 || IsClosed)
                {
                    throw new InvalidOperationException("连接已启动或已关闭，无法更改迟到答复处理方式。");
                }

                if (_lateReplyMode == LateReplyMode.Channel)
                {
                    throw new InvalidOperationException("连接已选择通道消费模式（已获取 LateReplies 读取端），禁止配置迟到答复回调。");
                }

                _lateReplyMode = LateReplyMode.Callback;
                _lateReplyHandler = value;
            }
        }
    }

    /// <summary>
    /// 启动后台读写双循环。必须在连接构造后显式调用一次。
    /// 先完整登记所有循环任务引用，再通过启动屏障释放循环开始执行 I/O。
    /// 若配置了 LateReplyHandler，则同时启动独立迟到答复消费循环。
    /// </summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_started != 0 || _terminating != 0 || IsClosed)
            {
                return;
            }

            _started = 1;
            if (_lateReplyMode == LateReplyMode.Unselected)
            {
                _lateReplyMode = LateReplyMode.Channel;
            }

            _outboundQueue.RegisterWriteLoop();
            _writeLoopTask = RunWriteLoopWrapperAsync();
            _readLoopTask = RunReadLoopAsync(_connectionCts.Token);
            if (_lateReplyMode == LateReplyMode.Callback && _lateReplyHandler is not null)
            {
                _lateRepliesLoopTask = RunLateRepliesLoopAsync(_connectionCts.Token);
            }
        }

        // 锁外释放启动屏障，允许所有循环开始执行 I/O
        _startBarrier.TrySetResult();
    }

    /// <summary>
    /// 发送一个标准协议信封。
    /// </summary>
    /// <param name="envelope">要发送的信封。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当信封成功写入底层流后完成的任务。</returns>
    /// <exception cref="ArgumentNullException">当 envelope 为 null 时抛出。</exception>
    /// <exception cref="InvalidOperationException">当连接尚未启动时抛出。</exception>
    /// <exception cref="ObjectDisposedException">当连接已关闭时抛出。</exception>
    public Task SendAsync(IpcEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (Volatile.Read(ref _started) == 0)
        {
            throw new InvalidOperationException("连接尚未启动。必须在发送消息前显式调用 Start()。");
        }

        if (IsClosed)
        {
            throw new ObjectDisposedException(nameof(IpcConnection), "连接已关闭。");
        }

        var isResultFrame = string.Equals(envelope.Type, ProtocolConstants.MessageTypes.ResultItem, StringComparison.Ordinal);
        return _outboundQueue.EnqueueFrameAsync(isResultFrame, () => IpcFrame.EncodeFrame(envelope), cancellationToken);
    }

    /// <summary>
    /// 快捷发送一元业务结果帧，由 Core 进行流式编码与信封组装。
    /// </summary>
    /// <typeparam name="TModel">业务输出数据类型（模型或 DTO）。</typeparam>
    /// <param name="payloadType">结果载荷类型名称。</param>
    /// <param name="model">业务输出数据实例。</param>
    /// <param name="jsonTypeInfo">业务载荷的 JSON 序列化类型元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当结果帧成功写入底层流后完成的任务。</returns>
    /// <exception cref="InvalidOperationException">当连接尚未启动时抛出。</exception>
    /// <exception cref="ObjectDisposedException">当连接已关闭时抛出。</exception>
    public Task SendSubmitResultAsync<TModel>(
        string payloadType,
        TModel model,
        JsonTypeInfo<TModel> jsonTypeInfo,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _started) == 0)
        {
            throw new InvalidOperationException("连接尚未启动。必须在发送消息前显式调用 Start()。");
        }

        if (IsClosed)
        {
            throw new ObjectDisposedException(nameof(IpcConnection), "连接已关闭。");
        }

        return _outboundQueue.EnqueueFrameAsync(
            isResultFrame: false,
            () => IpcFrame.EncodeSubmitResultFrame(payloadType, model, jsonTypeInfo),
            cancellationToken);
    }

    /// <summary>
    /// 快捷发送流式结果项帧。
    /// </summary>
    /// <typeparam name="TModel">流元素数据类型（模型或 DTO）。</typeparam>
    /// <param name="model">流元素数据实例。</param>
    /// <param name="jsonTypeInfo">流元素数据的 JSON 序列化类型元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当流元素成功写入底层流后完成的任务。</returns>
    /// <exception cref="InvalidOperationException">当连接尚未启动时抛出。</exception>
    /// <exception cref="ObjectDisposedException">当连接已关闭时抛出。</exception>
    public Task SendResultItemAsync<TModel>(
        TModel model,
        JsonTypeInfo<TModel> jsonTypeInfo,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _started) == 0)
        {
            throw new InvalidOperationException("连接尚未启动。必须在发送消息前显式调用 Start()。");
        }

        if (IsClosed)
        {
            throw new ObjectDisposedException(nameof(IpcConnection), "连接已关闭。");
        }

        return _outboundQueue.EnqueueFrameAsync(
            isResultFrame: true,
            () => IpcFrame.EncodeResultItemFrame(model, jsonTypeInfo),
            cancellationToken);
    }

    /// <summary>
    /// 发送请求信封并异步等待对应的匹配响应答复。
    /// 协调登记、出站写出、快速答复、取消与发送异常等所有竞争窗口。
    /// </summary>
    /// <param name="request">必须包含有效 Id 的请求信封。</param>
    /// <param name="cancellationToken">等待超时或取消令牌。</param>
    /// <returns>对端返回的匹配响应信封。</returns>
    /// <exception cref="ArgumentNullException">当 request 为 null 时抛出。</exception>
    /// <exception cref="ProtocolException">当 request.Id 为空时抛出。</exception>
    /// <exception cref="InvalidOperationException">当连接尚未启动时抛出。</exception>
    /// <exception cref="ObjectDisposedException">当连接已关闭时抛出。</exception>
    public async Task<IpcEnvelope> RequestAsync(IpcEnvelope request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Id is not { } requestId)
        {
            throw new ProtocolException($"请求消息 {request.Type} 缺少必需的 Id 字段。");
        }

        if (Volatile.Read(ref _started) == 0)
        {
            throw new InvalidOperationException("连接尚未启动。必须在发送请求前显式调用 Start()。");
        }

        if (IsClosed)
        {
            throw new ObjectDisposedException(nameof(IpcConnection), "连接已关闭。");
        }

        var (expectedType, altTypes) = ResolveExpectedResponses(request.Type);
        var pending = _requestMatcher.RegisterRequest(
            requestId,
            expectedType,
            cancellationToken,
            altTypes);

        try
        {
            // 将 pending.TryBeginWrite 作为 shouldWrite 谓词传入，与写入前取消执行同一次原子裁决
            await _outboundQueue.EnqueueFrameAsync(
                isResultFrame: false,
                () => IpcFrame.EncodeFrame(request),
                cancellationToken,
                shouldWrite: () => pending.TryBeginWrite()).ConfigureAwait(false);

            return await pending.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 将发送等待失败提交给 TrackedRequest 状态机统一裁决，避免以瞬时快照代替状态裁决
            return await pending.OnSendWaitFailedAsync(ex).ConfigureAwait(false);
        }
        finally
        {
            pending.Dispose();
        }
    }

    /// <summary>
    /// 主动发起关闭连接并指定终止原因。
    /// 触发排空出站队列并在限时内等待连接彻底关闭；超时后直接返回，内部收尾继续进行，调用方可通过 <see cref="WaitClosedAsync"/> 观察实际退出。
    /// </summary>
    /// <param name="reason">连接终止分类。</param>
    /// <param name="flushTimeout">排空出站队列及等待连接关闭的超时时限；为空或非 NormalClosed 则不等待排空，默认等待 2 秒。</param>
    /// <returns>发起关闭并在限时内等待的异步任务。</returns>
    public async Task CloseAsync(IpcTerminationReason reason = IpcTerminationReason.NormalClosed, TimeSpan? flushTimeout = null)
    {
        var waitTimeout = flushTimeout ?? TimeSpan.FromSeconds(2);
        var sw = Stopwatch.StartNew();

        if (reason == IpcTerminationReason.NormalClosed && !_outboundQueue.IsStopped)
        {
            try
            {
                var remaining = waitTimeout - sw.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await _outboundQueue.CompleteWritesAsync(remaining, CancellationToken.None).ConfigureAwait(false);
                }
                Terminate(reason);
            }
            catch (Exception ex)
            {
                // 排空期间发生真实写故障，保留真实故障而非被 NormalClosed 覆盖
                Terminate(IpcTerminationReason.Disconnected, ex);
            }
        }
        else
        {
            Terminate(reason);
        }

        // 调用方有限等待连接彻底关闭（涵盖取消回调、通道队列关闭、底层流处置与读写循环退出）；
        // 严格遵守统一总预算，超时后直接返回，内部收尾继续进行，调用方可通过 WaitClosedAsync 等待实际结束
        var remainingForClose = waitTimeout - sw.Elapsed;
        if (remainingForClose > TimeSpan.Zero)
        {
            using var timeoutCts = new CancellationTokenSource(remainingForClose);
            try
            {
                await _closedTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 超时仅限制调用方等待时长，内部收尾任务继续由组件持有并在后台安全观察
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// 异步等待连接彻底关闭并返回最终终止原因。
    /// 保证内部读写、派发、出站生产者与取消工作全部退出后才完成。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>连接终止的原因分类。</returns>
    public Task<IpcTerminationReason> WaitClosedAsync(CancellationToken cancellationToken = default)
    {
        return _closedTcs.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await CloseAsync(TerminationReason).ConfigureAwait(false);

        // 确保使用 DisposeAsync 正确释放出站队列
        try
        {
            await _outboundQueue.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordCleanupFailure(ex);
        }

        // 仅在任务真正彻底退出后安全释放内部同步原语与出站队列
        _disposalCleanupTask = _closedTcs.Task.ContinueWith(static async (_, state) =>
        {
            var self = (IpcConnection)state!;
            try { await self._outboundQueue.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { self.RecordCleanupFailure(ex); }
            if (self._outboundQueue.PrimitiveDisposalTask is { } primTask)
            {
                try { await primTask.ConfigureAwait(false); } catch (Exception ex) { self.RecordCleanupFailure(ex); }
            }
            try { self._streamState.Dispose(); } catch (Exception ex) { self.RecordCleanupFailure(ex); }
            try { self._requestMatcher.Dispose(); } catch (Exception ex) { self.RecordCleanupFailure(ex); }
            try { self._connectionCts.Dispose(); } catch (Exception ex) { self.RecordCleanupFailure(ex); }
        }, this, TaskScheduler.Default).Unwrap();
    }

    /// <summary>连接彻底退出后释放内部资源的延续任务。</summary>
    private Task? _disposalCleanupTask;

    /// <summary>
    /// 获取连接彻底退出后内部同步原语与出站队列完成释放的延续任务。
    /// </summary>
    public Task? DisposalCleanupTask => _disposalCleanupTask;

    /// <summary>
    /// 记录清理阶段发生的二次异常。
    /// </summary>
    private void RecordCleanupFailure(Exception failure)
    {
        Interlocked.CompareExchange(ref _cleanupFailure, failure, null);
    }

    /// <summary>
    /// 设置连接终止原因。首次记录的非正常原因作为根因被不可逆保留，后续异常记入清理故障。
    /// </summary>
    private void SetTerminationReason(IpcTerminationReason reason, Exception? failure = null)
    {
        if (reason == IpcTerminationReason.NormalClosed)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _abnormalReasonSet, 1, 0) == 0)
        {
            Volatile.Write(ref _terminationReason, (int)reason);
            _rootFailure = failure;
        }
        else if (failure is not null)
        {
            RecordCleanupFailure(failure);
        }
    }

    /// <summary>
    /// 解析请求类型的预期答复类型与可选备选响应类型。
    /// </summary>
    private static (string Expected, IReadOnlySet<string>? Alternatives) ResolveExpectedResponses(string requestType)
    {
        return requestType switch
        {
            ProtocolConstants.MessageTypes.LeaseRequest =>
                (ProtocolConstants.MessageTypes.LeaseGranted, new HashSet<string> { ProtocolConstants.MessageTypes.LeaseDenied }),
            ProtocolConstants.MessageTypes.ParamRequest =>
                (ProtocolConstants.MessageTypes.ParamValue, null),
            ProtocolConstants.MessageTypes.SourceRepair =>
                (ProtocolConstants.MessageTypes.SourceRepairResult, null),
            ProtocolConstants.MessageTypes.OperationRegister =>
                (ProtocolConstants.MessageTypes.OperationAccepted, null),
            ProtocolConstants.MessageTypes.PolicyUpdate =>
                (ProtocolConstants.MessageTypes.PolicyApplied, null),
            ProtocolConstants.MessageTypes.ChildStart =>
                (ProtocolConstants.MessageTypes.ChildStarted, null),
            ProtocolConstants.MessageTypes.ChildStop =>
                (ProtocolConstants.MessageTypes.ChildReaped, null),
            ProtocolConstants.MessageTypes.ChildMessage =>
                (ProtocolConstants.MessageTypes.ChildMessageReply, null),
            _ => (string.Empty, null)
        };
    }
}
