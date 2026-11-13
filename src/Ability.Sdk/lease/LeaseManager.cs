using System.Collections.Concurrent;
using Ability.Core;

namespace Ability.Sdk;

/// <summary>
/// 负责 SDK 端受管资源租约的状态追踪、释放调度及迟到答复回收。
/// 持有活动租约集合与待完成释放任务队列；提供有界超时的释放排空，避免会话终态无限挂起。
/// </summary>
internal sealed class LeaseManager
{
    /// <summary>
    /// 当前处于有效授权状态的活动租约编号集合。
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _activeLeaseIds = new(StringComparer.Ordinal);

    /// <summary>
    /// 状态同步与互斥裁决门控锁。
    /// </summary>
    private readonly object _stateLock = new();

    /// <summary>
    /// 正在追踪的未完成释放发送任务集合（涵盖等待、发送及结果，保留全部所有权）。
    /// </summary>
    private readonly HashSet<Task<bool>> _trackedOperations = new();

    /// <summary>
    /// 记录是否曾经发生过任何释放操作失败（保留已完成操作的失败结果）。
    /// </summary>
    private int _hasFailedReleases;

    /// <summary>
    /// 当前在途排队或正在执行的释放任务总数。
    /// </summary>
    private int _pendingReleaseCount;

    /// <summary>
    /// 释放排空状态等待源（当在途任务数归零时置位通知所有排空等待者）。
    /// </summary>
    private TaskCompletionSource<bool>? _idleTcs;

    /// <summary>
    /// 释放任务队列的最大上限，防止无界累积。
    /// </summary>
    private const int MaxPendingReleaseTasks = 256;

    /// <summary>
    /// 当前登记的活动租约编号快照。
    /// </summary>
    public IReadOnlyCollection<string> ActiveLeaseIds => _activeLeaseIds.Keys.ToArray();

    /// <summary>
    /// 是否曾经发生过任何租约释放操作失败。
    /// </summary>
    public bool HasFailedReleases => Volatile.Read(ref _hasFailedReleases) != 0;

    /// <summary>
    /// 登记新批准的租约并返回租约句柄。
    /// 若租约标识已存在则抛出协议异常，杜绝重复租约标识。
    /// </summary>
    /// <param name="leaseId">宿主分配的全局唯一租约编号。</param>
    /// <param name="connection">发送释放帧所依赖的 IPC 连接。</param>
    /// <returns>构建的租约句柄。</returns>
    /// <exception cref="ProtocolException">租约编号重复时抛出。</exception>
    public AbilityLease TrackLease(string leaseId, IpcConnection connection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
        ArgumentNullException.ThrowIfNull(connection);

        if (!_activeLeaseIds.TryAdd(leaseId, 0))
        {
            throw new ProtocolException($"宿主返回了重复的租约编号：'{leaseId}'。");
        }

        return new AbilityLease(leaseId, id => EnqueueRelease(id, connection));
    }

    /// <summary>
    /// 将指定租约的释放指令排入底层发送队列并登记追踪任务。
    /// 采用原子的容量预留与登记，超限时原子回退并标记失败直接返回。
    /// 所有已接收释放请求先取得可追踪的所有权，再开始执行；任务完成时同步从追踪集合移除并通知排空等待者。
    /// </summary>
    /// <param name="leaseId">租约全局唯一标识。</param>
    /// <param name="connection">底层双工通信连接。</param>
    public void EnqueueRelease(string leaseId, IpcConnection connection)
    {
        if (string.IsNullOrWhiteSpace(leaseId))
        {
            return;
        }

        _activeLeaseIds.TryRemove(leaseId, out _);

        TaskCompletionSource<bool> tcs;
        lock (_stateLock)
        {
            // 1. 原子预留容量，严格约束排队和执行总数不超过 256
            if (_pendingReleaseCount >= MaxPendingReleaseTasks)
            {
                _hasFailedReleases = 1;
                return;
            }

            _pendingReleaseCount++;
            if (_idleTcs is null || _idleTcs.Task.IsCompleted)
            {
                _idleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            // 2. 必须先取得可追踪的所有权，再启动执行
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _trackedOperations.Add(tcs.Task);
        }

        // 3. 启动执行任务，严格按序收口：记录失败 -> 移出追踪 -> 归还计数 -> 归零通知等待源 -> 完成任务源
        _ = Task.Run(async () =>
        {
            var success = false;
            try
            {
                success = await ExecuteReleaseOperationAsync(leaseId, connection).ConfigureAwait(false);
            }
            catch
            {
                success = false;
            }
            finally
            {
                lock (_stateLock)
                {
                    if (!success)
                    {
                        _hasFailedReleases = 1;
                    }

                    _trackedOperations.Remove(tcs.Task);
                    _pendingReleaseCount--;
                    if (_pendingReleaseCount == 0)
                    {
                        _idleTcs?.TrySetResult(true);
                    }
                }

                tcs.TrySetResult(success);
            }
        });
    }

    /// <summary>
    /// 异步执行单项租约释放操作。
    /// 涵盖发送信封及结果完成，发送取消源保留到发送实际结束。
    /// </summary>
    /// <param name="leaseId">租约编号。</param>
    /// <param name="connection">底层双工通信连接。</param>
    /// <returns>释放成功发送返回 true；失败返回 false。</returns>
    private static async Task<bool> ExecuteReleaseOperationAsync(string leaseId, IpcConnection connection)
    {
        if (connection is not { IsClosed: false })
        {
            return false;
        }

        try
        {
            if (connection.IsClosed)
            {
                return false;
            }

            using var sendCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await connection.SendAsync(ProtocolMessages.CreateLeaseRelease(leaseId), sendCts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 处理取消或超时后迟到到达的 LeaseGranted 答复。
    /// 立即发出释放帧并将其任务登记、观察与纳入收尾，杜绝未追踪租约泄漏。
    /// </summary>
    /// <param name="envelope">迟到的入站信封。</param>
    /// <param name="connection">底层双工通信连接。</param>
    public void HandleLateLeaseGranted(IpcEnvelope envelope, IpcConnection connection)
    {
        if (!string.Equals(envelope.Type, ProtocolConstants.MessageTypes.LeaseGranted, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var granted = ProtocolMessages.ReadBody<LeaseGrantedBody>(envelope);
            if (!string.IsNullOrWhiteSpace(granted.LeaseId))
            {
                EnqueueRelease(granted.LeaseId, connection);
            }
        }
        catch
        {
            // 迟到畸形消息安全忽略，不中断连接
        }
    }

    /// <summary>
    /// 在指定超时时间内等待所有排队的租约释放指令写出完成，确保释放先于终态送达。
    /// 采用统一状态等待机制，直接监听归零通知，消除忙循环，可靠响应期限与取消。
    /// 包含等待容量的释放操作，并保留已完成操作的失败结果，绝不提前报告成功。
    /// </summary>
    /// <param name="timeout">最大等待时限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>若所有排队的释放指令均成功排空写出返回 true；若发生超时或异常中断返回 false。</returns>
    public async Task<bool> FlushReleasesAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
        var token = linkedCts.Token;

        try
        {
            while (true)
            {
                Task waitTask;
                lock (_stateLock)
                {
                    if (_pendingReleaseCount == 0 && _trackedOperations.Count == 0)
                    {
                        return _hasFailedReleases == 0;
                    }

                    waitTask = _idleTcs?.Task ?? Task.CompletedTask;
                }

                await waitTask.WaitAsync(token).ConfigureAwait(false);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 等待当前所有排队的租约释放指令写出完成（默认 2 秒时限）。
    /// </summary>
    /// <returns>若全部写出返回 true；超时未完成返回 false。</returns>
    public Task<bool> FlushReleasesAsync()
        => FlushReleasesAsync(TimeSpan.FromSeconds(2), CancellationToken.None);

    /// <summary>
    /// 在取消令牌约束下等待所有排队的租约释放指令写出完成。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>若全部写出返回 true；发生取消或超时返回 false。</returns>
    public Task<bool> FlushReleasesAsync(CancellationToken cancellationToken)
        => FlushReleasesAsync(Timeout.InfiniteTimeSpan, cancellationToken);

    /// <summary>
    /// 向宿主异步申请指定受管资源的访问租约。
    /// 若在获批与交付之间发生取消，确保立即释放租约。
    /// </summary>
    /// <param name="connection">IPC 通信连接。</param>
    /// <param name="resource">申请的资源标识。</param>
    /// <param name="mode">租约访问模式。</param>
    /// <param name="businessCancellation">会话业务取消令牌。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <returns>租约访问句柄。</returns>
    public async Task<AbilityLease> AcquireLeaseAsync(
        IpcConnection connection,
        string resource,
        LeaseMode mode,
        CancellationToken businessCancellation,
        CancellationToken cancellationToken)
    {
        var requestId = connection.NextRequestId();
        var requestEnvelope = ProtocolMessages.CreateLeaseRequest(requestId, resource, mode.ToString());

        var response = await connection.RequestAsync(requestEnvelope, cancellationToken).ConfigureAwait(false);

        if (string.Equals(response.Type, ProtocolConstants.MessageTypes.LeaseGranted, StringComparison.Ordinal))
        {
            var granted = ProtocolMessages.ReadBody<LeaseGrantedBody>(response);
            if (string.IsNullOrWhiteSpace(granted.LeaseId))
            {
                throw new ProtocolException("LeaseGranted 答复缺少有效的 leaseId。");
            }

            var lease = TrackLease(granted.LeaseId, connection);

            // 若在获得批准与交付业务之间已发生取消，必须立即释放租约，杜绝资源泄漏
            if (cancellationToken.IsCancellationRequested || businessCancellation.IsCancellationRequested)
            {
                lease.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(businessCancellation);
            }

            return lease;
        }

        if (string.Equals(response.Type, ProtocolConstants.MessageTypes.LeaseDenied, StringComparison.Ordinal))
        {
            var denied = ProtocolMessages.ReadBody<LeaseDeniedBody>(response);
            throw new LeaseDeniedException(resource, denied.Reason);
        }

        if (string.Equals(response.Type, ProtocolConstants.MessageTypes.RequestFault, StringComparison.Ordinal))
        {
            var fault = ProtocolMessages.ReadBody<RequestFaultBody>(response);
            throw new AbilityRequestFaultException(fault.Kind, fault.Message, site: "LeaseRequest");
        }

        throw new ProtocolException($"租约申请收到意外答复 '{response.Type}'。");
    }

    /// <summary>
    /// 统一释放所有剩余活动租约并在有限时限内排空队列。
    /// 消除无界或无限等待，保证通信连接与本地登记能安全收口。
    /// </summary>
    /// <param name="connection">底层双工通信连接。</param>
    public async ValueTask DisposeAsync(IpcConnection? connection)
    {
        if (connection is { IsClosed: false })
        {
            var remainingLeases = _activeLeaseIds.Keys.ToArray();
            foreach (var leaseId in remainingLeases)
            {
                EnqueueRelease(leaseId, connection);
            }
        }
        else if (!_activeLeaseIds.IsEmpty)
        {
            // 连接已关闭但仍有未释放租约，标记失败并清空活动表
            lock (_stateLock)
            {
                _hasFailedReleases = 1;
            }
            _activeLeaseIds.Clear();
        }

        await FlushReleasesAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
    }
}
