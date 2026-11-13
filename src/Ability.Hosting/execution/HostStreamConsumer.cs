using System.Threading.Channels;
using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// 宿主端流式结果消费器，负责在后台独立读取流元素、调用消费回调、返还信用额度并完整追踪回调及收尾任务退出。
/// </summary>
internal sealed class HostStreamConsumer
{
    /// <summary>默认外部消费与回调超时时限。</summary>
    private static readonly TimeSpan DefaultCallbackTimeout = TimeSpan.FromSeconds(30);

    /// <summary>结果项有界通道读取器。</summary>
    private readonly ChannelReader<ResultItemBody> _reader;
    /// <summary>底层双工通信连接。</summary>
    private readonly IpcConnection _connection;
    /// <summary>宿主运行选项配置。</summary>
    private readonly HostRuntimeOptions _options;
    /// <summary>获取当前流输出类型名称的委托。</summary>
    private readonly Func<string?> _getOutputTypeName;
    /// <summary>写出信用额度的有限超时时限。</summary>
    private readonly TimeSpan _sendTimeout;
    /// <summary>操作取消令牌。</summary>
    private readonly CancellationToken _cancellationToken;
    /// <summary>分离的异步收尾与回调任务列表。</summary>
    private readonly List<Task> _detachedCleanupTasks = [];
    /// <summary>收尾任务状态锁。</summary>
    private readonly Lock _lock = new();
    /// <summary>所有分离任务完成排空源。</summary>
    private readonly TaskCompletionSource _allDetachedTasksDrainedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>活跃的分离异步任务计数。</summary>
    private int _activeDetachedTasks;
    /// <summary>主读取循环是否已完成。</summary>
    private bool _runLoopCompleted;

    /// <summary>
    /// 初始化宿主端流式结果消费器实例。
    /// </summary>
    /// <param name="reader">结果项有界通道读取器。</param>
    /// <param name="connection">底层双工通信连接。</param>
    /// <param name="options">宿主运行选项配置。</param>
    /// <param name="getOutputTypeName">获取当前流输出类型名称的委托。</param>
    /// <param name="sendTimeout">写出信用额度的有限超时时限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public HostStreamConsumer(
        ChannelReader<ResultItemBody> reader,
        IpcConnection connection,
        HostRuntimeOptions options,
        Func<string?> getOutputTypeName,
        TimeSpan sendTimeout,
        CancellationToken cancellationToken)
    {
        _reader = reader;
        _connection = connection;
        _options = options;
        _getOutputTypeName = getOutputTypeName;
        _sendTimeout = sendTimeout;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// 登记一个脱离前台循环的异步回调、取消任务及取消源释放延续，纳入物理退出等待。
    /// </summary>
    /// <param name="task">脱离的清理任务。</param>
    private void TrackDetachedCleanupTask(Task task)
    {
        lock (_lock)
        {
            _detachedCleanupTasks.Add(task);
            _activeDetachedTasks++;
        }

        _ = task.ContinueWith(static (_, state) =>
        {
            var self = (HostStreamConsumer)state!;
            lock (self._lock)
            {
                self._activeDetachedTasks--;
                if (self._runLoopCompleted && self._activeDetachedTasks <= 0)
                {
                    self._allDetachedTasksDrainedTcs.TrySetResult();
                }
            }
        }, this, TaskScheduler.Default);
    }

    /// <summary>
    /// 等待消费工作循环与所有脱离回调、取消及释放延续任务完全物理退出。
    /// </summary>
    /// <returns>等待完全退出的异步任务。</returns>
    public async Task WaitForPhysicalExitAsync()
    {
        Task drainTask;
        lock (_lock)
        {
            if (_runLoopCompleted && _activeDetachedTasks <= 0)
            {
                return;
            }
            drainTask = _allDetachedTasksDrainedTcs.Task;
        }

        try
        {
            await drainTask.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 获取消费器完全物理退出（包括消费循环、回调延续与取消任务及令牌源释放）的异步任务。
    /// </summary>
    public Task PhysicalExitTask => WaitForPhysicalExitAsync();

    /// <summary>
    /// 后台异步消费结果项并按需向技能端回发信用额度。
    /// </summary>
    /// <returns>异步消费循环任务。</returns>
    public async Task RunLoopAsync()
    {
        try
        {
            while (await _reader.WaitToReadAsync(_cancellationToken).ConfigureAwait(false))
            {
                while (_reader.TryRead(out var item))
                {
                    if (_options.ResultItemCallback is not null)
                    {
                        await InvokeResultItemCallbackAsync(item).ConfigureAwait(false);
                    }

                    await GrantConsumedCreditAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                _runLoopCompleted = true;
                if (_activeDetachedTasks <= 0)
                {
                    _allDetachedTasksDrainedTcs.TrySetResult();
                }
            }
        }
    }

    /// <summary>
    /// 执行一次结果项消费回调：施加单次回调期限、父级取消与同步门控，迟到清理由后台任务持续追踪。
    /// </summary>
    /// <param name="item">待消费的结果项。</param>
    /// <returns>回调执行的异步完成任务。</returns>
    private async Task InvokeResultItemCallbackAsync(ResultItemBody item)
    {
        var cancelGate = new object();
        var cancelClosed = false;
        Task? cancelTask = null;
        var cbCts = new CancellationTokenSource();

        void TriggerAsyncCancellation()
        {
            lock (cancelGate)
            {
                if (cancelClosed || cancelTask is not null)
                {
                    return;
                }

                cancelTask = Task.Run(() =>
                {
                    try { cbCts.Cancel(); } catch (ObjectDisposedException) { }
                });
            }
        }

        using var timeoutTimer = _options.TimeProvider.CreateTimer(
            static state => ((Action)state!)(),
            (Action)TriggerAsyncCancellation,
            DefaultCallbackTimeout,
            Timeout.InfiniteTimeSpan);

        using var parentReg = _cancellationToken.UnsafeRegister(static state =>
        {
            ((Action)state!)();
        }, (Action)TriggerAsyncCancellation);

        var cbToken = cbCts.Token;
        var cbTask = Task.Run(async () =>
        {
            await _options.ResultItemCallback!(
                new StreamResultItem(_getOutputTypeName() ?? "Item", item.Payload),
                cbToken).ConfigureAwait(false);
        }, cbToken);

        try
        {
            await cbTask.WaitAsync(DefaultCallbackTimeout, _options.TimeProvider, _cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TriggerAsyncCancellation();
            throw;
        }
        finally
        {
            timeoutTimer.Dispose();
            parentReg.Dispose();

            Task? finalCancelTask;
            lock (cancelGate)
            {
                cancelClosed = true;
                finalCancelTask = cancelTask;
            }

            var tasksToAwait = finalCancelTask is not null
                ? [cbTask, finalCancelTask]
                : new[] { cbTask };

            var detachedTask = Task.WhenAll(tasksToAwait).ContinueWith(static (t, state) =>
            {
                _ = t.Exception;
                var c = (CancellationTokenSource)state!;
                try { c.Dispose(); } catch { }
            }, cbCts, TaskScheduler.Default);

            TrackDetachedCleanupTask(detachedTask);
        }
    }

    /// <summary>
    /// 记录一项已消费结果并回发一个信用额度；发送失败时撤回本次额度。
    /// </summary>
    /// <returns>信用回发的异步完成任务。</returns>
    private async Task GrantConsumedCreditAsync()
    {
        _connection.StreamState.RecordConsumedItem();
        _connection.StreamState.GrantCredit(1);
        using var sendCts = new CancellationTokenSource(_sendTimeout, _options.TimeProvider);
        using var linkedSendCts = CancellationTokenSource.CreateLinkedTokenSource(sendCts.Token, _cancellationToken);
        try
        {
            await _connection.SendAsync(ProtocolMessages.CreateResultCredit(1), linkedSendCts.Token).ConfigureAwait(false);
        }
        catch
        {
            _connection.StreamState.RevokeCredit(1);
            throw;
        }
    }
}
