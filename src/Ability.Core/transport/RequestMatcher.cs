using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Ability.Core;

/// <summary>
/// 请求匹配器：在发送前登记请求编号与预期答复类型，通过原子状态机裁决发送、答复、取消与断连竞争，并提供有界的迟到答复识别。
/// </summary>
public sealed class RequestMatcher : IDisposable
{
    /// <summary>
    /// 同时在途的最大未决请求数量上限。
    /// </summary>
    public const int MaxPendingRequests = 64;

    /// <summary>
    /// 未决请求并发映射表。
    /// </summary>
    private readonly ConcurrentDictionary<long, TrackedRequest> _pending = new();

    /// <summary>
    /// 单调递增的请求编号计数器。
    /// </summary>
    private long _nextRequestId;

    /// <summary>
    /// 当前在途未决请求原子计数。
    /// </summary>
    private int _activeCount;

    /// <summary>
    /// 匹配器资源释放状态（0 为未释放，1 为已释放）。
    /// </summary>
    private int _disposed;

    /// <summary>
    /// 生成下一个单调递增的请求编号。
    /// </summary>
    /// <returns>全局唯一的请求 ID。</returns>
    public long NextRequestId() => Interlocked.Increment(ref _nextRequestId);

    /// <summary>
    /// 当前在途未决请求总数。
    /// </summary>
    public int ActiveCount => Volatile.Read(ref _activeCount);

    /// <summary>
    /// 在发送请求前原子检查容量并登记等待者。
    /// </summary>
    /// <param name="requestId">请求唯一编号。</param>
    /// <param name="expectedResponseType">预期的主要响应消息类型名称。</param>
    /// <param name="cancellationToken">该请求等待的取消令牌。</param>
    /// <param name="alternativeResponseTypes">可选的其他允许响应类型（如 LeaseDenied 等）。</param>
    /// <returns>在途请求强类型生命周期跟踪实例。</returns>
    /// <exception cref="ProtocolException">当请求编号重复或未决请求达到上限时抛出。</exception>
    public TrackedRequest RegisterRequest(
        long requestId,
        string expectedResponseType,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? alternativeResponseTypes = null)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedResponseType);

        // 原子检查并占用容量名额
        if (Interlocked.Increment(ref _activeCount) > MaxPendingRequests)
        {
            Interlocked.Decrement(ref _activeCount);
            throw new ProtocolException($"活动未决请求已达容量上限 ({MaxPendingRequests})，拒绝新请求。");
        }

        var entry = new TrackedRequest(this, requestId, expectedResponseType, alternativeResponseTypes, cancellationToken);
        if (!_pending.TryAdd(requestId, entry))
        {
            Interlocked.Decrement(ref _activeCount);
            throw new ProtocolException($"请求编号 {requestId} 已存在，不可重复登记。");
        }

        entry.InitializeCancellation();
        return entry;
    }

    /// <summary>
    /// 尝试将入站信封与已登记的请求进行匹配。核验预期消息类型与状态流转。
    /// </summary>
    /// <param name="envelope">收到的入站信封。</param>
    /// <param name="isLateReply">若为迟到答复（原请求已被取消），输出为 true；正常答复输出为 false。</param>
    /// <returns>若匹配成功返回 true；若不存在对应请求或类型不符返回 false。</returns>
    public bool TryMatchResponse(IpcEnvelope envelope, out bool isLateReply)
    {
        isLateReply = false;
        if (envelope.Id is not { } responseId)
        {
            return false;
        }

        if (!_pending.TryGetValue(responseId, out var entry))
        {
            return false;
        }

        // 校验响应类型：必须为预期类型、备选类型，或通用的 RequestFault
        if (!entry.IsAcceptedType(envelope.Type))
        {
            return false;
        }

        // 委托 TrackedRequest 内部执行原子状态流转
        if (entry.TrySetCompleted(envelope))
        {
            RemoveEntry(responseId);
            return true;
        }

        if (entry.TrySetLateReplyHandled())
        {
            RemoveEntry(responseId);
            isLateReply = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 从活动登记表中移除指定请求并归还活动名额。
    /// </summary>
    /// <param name="requestId">请求唯一编号。</param>
    internal void RemoveEntry(long requestId)
    {
        if (_pending.TryRemove(requestId, out _))
        {
            Interlocked.Decrement(ref _activeCount);
        }
    }

    /// <summary>
    /// 连接中断或关闭时，终止所有正在等待的请求，保证所有等待者均被唤醒并移除。
    /// </summary>
    /// <param name="exception">导致取消的根本异常。</param>
    public void CancelAll(Exception exception)
    {
        foreach (var (requestId, entry) in _pending.ToArray())
        {
            entry.AbortOnConnectionClose(exception);
            RemoveEntry(requestId);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancelAll(new ObjectDisposedException(nameof(RequestMatcher), "请求匹配器已释放。"));
    }

    /// <summary>
    /// 若当前匹配器已释放则抛出 ObjectDisposedException。
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(RequestMatcher));
        }
    }
}

/// <summary>
/// 单个在途请求的强类型生命周期记录，协调登记、实际发送、答复、取消和发送失败。
/// </summary>
public sealed class TrackedRequest : IDisposable
{
    /// <summary>已登记，未确认实际向底层流写出。</summary>
    public const int StateRegistered = 0;
    /// <summary>已裁决写入，正在或已经向底层流输出（物理发往对端）。</summary>
    public const int StateSent = 1;
    /// <summary>已匹配答复并成功完成。</summary>
    public const int StateCompleted = 2;
    /// <summary>实际写出后被调用方取消（保留条目以待迟到答复）。</summary>
    public const int StateCancelled = 3;
    /// <summary>写出前取消或发送失败，已彻底中止（已从字典移除并归还槽位）。</summary>
    public const int StateAborted = 4;
    /// <summary>迟到答复已处理并回收。</summary>
    public const int StateLateReplyHandled = 5;

    /// <summary>所属的请求匹配器实例。</summary>
    private readonly RequestMatcher _matcher;
    /// <summary>备选合法答复类型集合（如 PolicyUpdate 允许同时接受 PolicyApplied 或 OperationExpired）。</summary>
    private readonly IReadOnlySet<string>? _alternativeResponseTypes;
    /// <summary>调用方等待取消令牌。</summary>
    private readonly CancellationToken _cancellationToken;
    /// <summary>请求完成源。</summary>
    private readonly TaskCompletionSource<IpcEnvelope> _completion;
    /// <summary>取消注册句柄。</summary>
    private CancellationTokenRegistration _cancellationReg;
    /// <summary>原子生命周期状态机值。</summary>
    private int _state;

    /// <summary>请求唯一编号。</summary>
    public long RequestId { get; }

    /// <summary>预期答复消息类型名。</summary>
    public string ExpectedResponseType { get; }

    /// <summary>用于等待响应的只读异步任务。</summary>
    public Task<IpcEnvelope> Task => _completion.Task;

    /// <summary>当前请求内部原子状态。</summary>
    public int State => Volatile.Read(ref _state);

    /// <summary>
    /// 初始化在途请求记录实例。
    /// </summary>
    /// <param name="matcher">所属请求匹配器。</param>
    /// <param name="requestId">请求唯一编号。</param>
    /// <param name="expectedResponseType">期望响应类型。</param>
    /// <param name="alternativeResponseTypes">备选响应类型集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    internal TrackedRequest(
        RequestMatcher matcher,
        long requestId,
        string expectedResponseType,
        IReadOnlySet<string>? alternativeResponseTypes,
        CancellationToken cancellationToken)
    {
        _matcher = matcher;
        RequestId = requestId;
        ExpectedResponseType = expectedResponseType;
        _alternativeResponseTypes = alternativeResponseTypes;
        _cancellationToken = cancellationToken;
        _completion = new TaskCompletionSource<IpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _completion.Task.ContinueWith(
            static task => { _ = task.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        _state = StateRegistered;
    }

    /// <summary>
    /// 注册取消令牌回调。
    /// </summary>
    internal void InitializeCancellation()
    {
        if (_cancellationToken.CanBeCanceled)
        {
            _cancellationReg = _cancellationToken.Register(static state =>
            {
                var self = (TrackedRequest)state!;
                self.OnCancelled();
            }, this);
        }
    }

    /// <summary>
    /// 尝试取得开始向底层流写出的权利。与写入前取消执行同一次原子裁决。
    /// </summary>
    /// <returns>若写入胜出返回 true；若取消胜出或已中止返回 false。</returns>
    internal bool TryBeginWrite()
    {
        return Interlocked.CompareExchange(ref _state, StateSent, StateRegistered) == StateRegistered;
    }

    /// <summary>
    /// 尝试以正常响应完成请求。仅在 Sent 状态下有效。
    /// 若处于 Registered 状态（尚未开始向底层流写出），收到答复属于严重协议时序违规，抛出 ProtocolException。
    /// </summary>
    /// <param name="envelope">收到的响应信封。</param>
    /// <returns>若成功流转为完成状态返回 true；否则返回 false。</returns>
    /// <exception cref="ProtocolException">当在 Registered 状态下收到对端答复时抛出。</exception>
    internal bool TrySetCompleted(IpcEnvelope envelope)
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if (state == StateRegistered)
            {
                throw new ProtocolException($"收到尚未开始写出的请求 (Id: {RequestId}) 的答复，违反协议时序。");
            }

            if (state != StateSent)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _state, StateCompleted, StateSent) == StateSent)
            {
                _completion.TrySetResult(envelope);
                return true;
            }
        }
    }

    /// <summary>
    /// 尝试处理迟到答复。仅在 Cancelled 状态下有效。
    /// </summary>
    /// <returns>若成功流转为迟到处理状态返回 true；否则返回 false。</returns>
    internal bool TrySetLateReplyHandled()
    {
        return Interlocked.CompareExchange(ref _state, StateLateReplyHandled, StateCancelled) == StateCancelled;
    }

    /// <summary>
    /// 当发送等待失败（被取消或抛出发送异常）时，将失败提交给状态机统一裁决。
    /// 若状态机已裁决为 Completed，等待答复发布并返回结果；
    /// 尚未裁决时，执行原子流转并抛出原异常，避免以瞬时快照代替状态裁决。
    /// </summary>
    /// <param name="exception">导致发送等待失败的异常。</param>
    /// <returns>若答复胜出返回匹配的响应信封；否则重新抛出原失败异常。</returns>
    internal async Task<IpcEnvelope> OnSendWaitFailedAsync(Exception exception)
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);

            // 1. 已经裁决为 Completed：答复胜出，等待答复任务发布完成并返回结果
            if (state == StateCompleted)
            {
                return await _completion.Task.ConfigureAwait(false);
            }

            // 2. 已经裁决为中止或迟到处理：直接重新抛出原异常
            if (state is StateAborted or StateLateReplyHandled)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
                throw exception;
            }

            // 3. 已经裁决为取消（且已进入发送）：若为非取消的发送异常则清理条目，并重新抛出原异常
            if (state == StateCancelled)
            {
                if (exception is not OperationCanceledException)
                {
                    _matcher.RemoveEntry(RequestId);
                }

                ExceptionDispatchInfo.Capture(exception).Throw();
                throw exception;
            }

            // 4. 尚未裁决（StateRegistered 或 StateSent），提交统一的原子流转
            if (exception is OperationCanceledException)
            {
                if (state == StateRegistered)
                {
                    // 尚未实际向底层流写出：原子迁移至 Aborted 并清理条目与名额
                    if (Interlocked.CompareExchange(ref _state, StateAborted, StateRegistered) == StateRegistered)
                    {
                        _completion.TrySetCanceled(_cancellationToken);
                        _matcher.RemoveEntry(RequestId);
                        ExceptionDispatchInfo.Capture(exception).Throw();
                    }
                    // CAS 失败说明并发流转为 StateCompleted 或 StateSent，继续循环由状态机裁决
                }
                else // state == StateSent
                {
                    // 已进入写入阶段后取消：原子迁移至 Cancelled，保留条目以待迟到答复
                    if (Interlocked.CompareExchange(ref _state, StateCancelled, StateSent) == StateSent)
                    {
                        _completion.TrySetCanceled(_cancellationToken);
                        ExceptionDispatchInfo.Capture(exception).Throw();
                    }
                    // CAS 失败说明并发流转为 StateCompleted，继续循环由状态机裁决
                }
            }
            else
            {
                // 发送或排队异常（非取消）：原子迁移至 Aborted 并清理条目与名额
                if (Interlocked.CompareExchange(ref _state, StateAborted, state) == state)
                {
                    _completion.TrySetException(exception);
                    _matcher.RemoveEntry(RequestId);
                    ExceptionDispatchInfo.Capture(exception).Throw();
                }
                // CAS 失败说明并发流转为 StateCompleted，继续循环由状态机裁决
            }
        }
    }

    /// <summary>
    /// 连接关闭时终止请求。
    /// </summary>
    /// <param name="exception">导致连接关闭的异常。</param>
    internal void AbortOnConnectionClose(Exception exception)
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if (state is StateCompleted or StateAborted or StateLateReplyHandled or StateCancelled)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _state, StateAborted, state) == state)
            {
                _completion.TrySetException(exception);
                return;
            }
        }
    }

    /// <summary>
    /// 当请求关联的取消令牌触发时执行状态流转与资源清理。
    /// </summary>
    private void OnCancelled()
    {
        // 若仍为 Registered（尚未实际向底层流写入），原子流转为 Aborted，彻底清理，不留迟到记录且归还槽位
        if (Interlocked.CompareExchange(ref _state, StateAborted, StateRegistered) == StateRegistered)
        {
            _completion.TrySetCanceled(_cancellationToken);
            _matcher.RemoveEntry(RequestId);
            return;
        }

        // 若已为 Sent（请求已取得写入权正在写入或已写出），原子流转为 Cancelled，保留记录以待迟到答复回收资源
        if (Interlocked.CompareExchange(ref _state, StateCancelled, StateSent) == StateSent)
        {
            _completion.TrySetCanceled(_cancellationToken);
        }
    }

    /// <summary>
    /// 校验入站信封类型是否为本请求允许的响应类型。
    /// </summary>
    /// <param name="responseType">收到的入站消息类型。</param>
    /// <returns>若被允许返回 true；否则返回 false。</returns>
    public bool IsAcceptedType(string responseType)
    {
        if (string.Equals(responseType, ExpectedResponseType, StringComparison.Ordinal)
            || string.Equals(responseType, ProtocolConstants.MessageTypes.RequestFault, StringComparison.Ordinal))
        {
            return true;
        }

        return _alternativeResponseTypes is not null && _alternativeResponseTypes.Contains(responseType);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cancellationReg.Dispose();
        var state = Volatile.Read(ref _state);
        if (state is StateCompleted or StateAborted)
        {
            _matcher.RemoveEntry(RequestId);
        }
    }
}
