using Ability.Core;

namespace Ability.Sdk;

/// <summary>
/// 将操作进度合并压缩到有界阶段表并在后台限频异步发送的进度通道。
/// 避免业务计算线程因直接等待 IPC 写帧而阻塞；同一阶段的更新就地覆盖，跨尝试代次核验防止旧进度外流。
/// </summary>
internal sealed class ProgressChannel : IProgress<RunProgress>, IAsyncDisposable
{
    /// <summary>
    /// 待发送进度阶段表的最大容量上限。超过此上限时淘汰最早登记的阶段。
    /// </summary>
    private const int MaxPendingStages = 32;

    /// <summary>
    /// 异步发送 IPC 信封的委托。
    /// </summary>
    private readonly Func<IpcEnvelope, CancellationToken, Task> _sendAction;

    /// <summary>
    /// 关联的操作键。
    /// </summary>
    private readonly string _operationKey;

    /// <summary>
    /// 用于时间戳采样与限频等待的时间提供者。
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 本地进程内可选的进度观察者。
    /// </summary>
    private readonly IProgress<RunProgress>? _observer;

    /// <summary>
    /// 读取当前尝试代次的委托。
    /// </summary>
    private readonly Func<long>? _generationProvider;

    /// <summary>
    /// 按阶段名称索引的待发进度缓冲区。
    /// </summary>
    private readonly Dictionary<string, PendingProgress> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// 保护进度字典与停止状态的同步锁。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// 进度发送与尝试结束的排他顺序裁决门控锁。
    /// </summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>
    /// 唤醒后台发送泵的单许可信号量。
    /// </summary>
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>
    /// 限制在途并发观察者派发任务数量的门控信号量（最大 4 个并发派发）。
    /// </summary>
    private readonly SemaphoreSlim _observerGate = new(4, 4);

    /// <summary>
    /// 活跃使用尝试结束门控的调用者计数。
    /// </summary>
    private int _activeSenders;

    /// <summary>
    /// 活跃在途的进度观察者后台派发任务计数。
    /// </summary>
    private int _activeObservers;

    /// <summary>
    /// 触发通道停止与后台泵取消的取消令牌源。
    /// </summary>
    private readonly CancellationTokenSource _stop = new();

    /// <summary>
    /// 两次进度发送之间的最小限频时间间隔。
    /// </summary>
    private TimeSpan _interval;

    /// <summary>
    /// 后台发送泵的长任务引用。
    /// </summary>
    private Task? _pumpTask;

    /// <summary>
    /// 单调递增的进度帧序号。
    /// </summary>
    private long _sequence;

    /// <summary>
    /// 标记通道是否已停止的原子变量（0 表示运行中，1 表示已停止）。
    /// </summary>
    private int _stopped;

    /// <summary>
    /// 初始化进度通道并启动后台限频发送泵。
    /// 支持受管操作级进度与会话级进度（允许操作键为空字符串）。
    /// </summary>
    /// <param name="sendAction">发送协议信封的委托。</param>
    /// <param name="operationKey">关联的操作键（会话级进度可传空字符串）。</param>
    /// <param name="interval">两次进度发送的最小限频时间间隔。</param>
    /// <param name="timeProvider">时间提供者。</param>
    /// <param name="observer">本地观察者回调（可选）。</param>
    /// <param name="generationProvider">尝试代次提供者（可选）。</param>
    public ProgressChannel(
        Func<IpcEnvelope, CancellationToken, Task> sendAction,
        string operationKey,
        TimeSpan interval,
        TimeProvider timeProvider,
        IProgress<RunProgress>? observer = null,
        Func<long>? generationProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sendAction);
        ArgumentNullException.ThrowIfNull(operationKey);

        _sendAction = sendAction;
        _operationKey = operationKey;
        _interval = interval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _observer = observer;
        _generationProvider = generationProvider;

        _pumpTask = PumpAsync();
    }

    /// <summary>
    /// 动态更新发送限频时间间隔。
    /// </summary>
    /// <param name="interval">新的限频间隔。</param>
    public void UpdateInterval(TimeSpan interval)
    {
        _interval = interval;
        SignalPump();
    }

    /// <summary>
    /// 记录一个阶段的最新进度。同阶段直接覆盖旧值；阶段表达到 32 上限时淘汰最早登记的阶段。
    /// </summary>
    /// <param name="value">进度数据模型。</param>
    public void Report(RunProgress value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Stage);

        if (Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_stopped != 0)
            {
                return;
            }

            if (!_pending.ContainsKey(value.Stage) && _pending.Count >= MaxPendingStages)
            {
                using var enumerator = _pending.Keys.GetEnumerator();
                if (enumerator.MoveNext())
                {
                    _pending.Remove(enumerator.Current);
                }
            }

            var generation = _generationProvider?.Invoke() ?? 0;
            _pending[value.Stage] = new PendingProgress(generation, value);
        }

        SignalPump();
    }

    /// <summary>
    /// 丢弃当前阶段表中尚未发出的所有进度。
    /// 当单次尝试结束或触发到期时调用，避免残留进度污染后续尝试。
    /// </summary>
    public void DiscardPending()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }

    /// <summary>
    /// 采用共同顺序裁决写出尝试结束信封。
    /// 清空未发出的残留进度，在排他门控下等待可能正在发送的进度帧完成，严格阻止旧进度越过 AttemptEnded 或进入后续尝试。
    /// 若门控等待超时抛出 TimeoutException，绝不绕过门控强行发送。
    /// </summary>
    /// <param name="attemptEndedAction">发送尝试结束信封的异步委托。</param>
    public async Task SendAttemptEndedAsync(Func<Task> attemptEndedAction)
    {
        ArgumentNullException.ThrowIfNull(attemptEndedAction);

        // 先原子核验通道运行状态，已关闭时拒绝进入，杜绝关闭后新增发送者
        lock (_gate)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                throw new ObjectDisposedException(nameof(ProgressChannel), "进度通道已关闭，无法发送尝试结束信封。");
            }

            Interlocked.Increment(ref _activeSenders);
        }

        DiscardPending();
        var acquired = false;
        try
        {
            acquired = await _sendGate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (!acquired)
            {
                throw new TimeoutException("等待进度通道门控超时，无法安全排序尝试结束信封。");
            }

            await attemptEndedAction().ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
            {
                _sendGate.Release();
            }

            Interlocked.Decrement(ref _activeSenders);
        }
    }

    /// <summary>
    /// 停止进度通道并等待后台泵、所有使用门控的调用者及活动观察者完全退出。
    /// 同步对象在实际使用者完全退出后安全释放，避免并发释放竞态。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            _pending.Clear();
        }

        _stop.Cancel();

        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        var spinStart = _timeProvider.GetTimestamp();
        while (Volatile.Read(ref _activeSenders) > 0 || Volatile.Read(ref _activeObservers) > 0)
        {
            if (_timeProvider.GetElapsedTime(spinStart) > TimeSpan.FromSeconds(2))
            {
                break;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), _timeProvider).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
        }

        var allUsersExited = (_pumpTask is { IsCompleted: true } or null)
            && Volatile.Read(ref _activeSenders) == 0
            && Volatile.Read(ref _activeObservers) == 0;

        if (allUsersExited)
        {
            _stop.Dispose();
            _signal.Dispose();
            _sendGate.Dispose();
            _observerGate.Dispose();
        }
        else
        {
            // 存在迟到发送者或慢观察者：挂接后台安全任务，直到全部退出后再释放同步对象，并安全观察异常
            _ = Task.Run(async () =>
            {
                if (_pumpTask is not null)
                {
                    try { await _pumpTask.ConfigureAwait(false); } catch { }
                }

                while (Volatile.Read(ref _activeSenders) > 0 || Volatile.Read(ref _activeObservers) > 0)
                {
                    try { await Task.Delay(50).ConfigureAwait(false); } catch { break; }
                }

                try { _stop.Dispose(); } catch { }
                try { _signal.Dispose(); } catch { }
                try { _sendGate.Dispose(); } catch { }
                try { _observerGate.Dispose(); } catch { }
            });
        }
    }

    /// <summary>
    /// 尝试释放信号量以唤醒后台发送泵。
    /// 若信号量计数已满或对象已释放则安全忽略。
    /// </summary>
    private void SignalPump()
    {
        try
        {
            if (_signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// 后台发送泵主循环。按设定的限频间隔拉取阶段缓冲区并发送进度帧。
    /// </summary>
    private async Task PumpAsync()
    {
        var lastSent = _timeProvider.GetTimestamp();
        var hasSent = false;

        while (true)
        {
            try
            {
                await _signal.WaitAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var interval = _interval < TimeSpan.FromMilliseconds(1)
                ? TimeSpan.FromMilliseconds(1)
                : _interval;

            if (hasSent)
            {
                var elapsed = _timeProvider.GetElapsedTime(lastSent);
                var remaining = interval - elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(remaining, _timeProvider, _stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }

            PendingProgress? next = null;
            lock (_gate)
            {
                using var enumerator = _pending.GetEnumerator();
                if (enumerator.MoveNext())
                {
                    next = enumerator.Current.Value;
                    _pending.Remove(enumerator.Current.Key);
                }
            }

            if (next is null)
            {
                continue;
            }

            if (Volatile.Read(ref _stopped) != 0)
            {
                return;
            }

            if (_generationProvider is not null && next.Generation != _generationProvider())
            {
                lock (_gate)
                {
                    if (_pending.Count > 0)
                    {
                        SignalPump();
                    }
                }
                continue;
            }

            var gateAcquired = false;
            try
            {
                gateAcquired = await _sendGate.WaitAsync(TimeSpan.FromSeconds(2), _stop.Token).ConfigureAwait(false);
                if (!gateAcquired)
                {
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (_generationProvider is not null && next.Generation != _generationProvider())
                {
                    lock (_gate)
                    {
                        if (_pending.Count > 0)
                        {
                            SignalPump();
                        }
                    }
                    continue;
                }

                if (Volatile.Read(ref _stopped) == 0 && _observer is not null && _observerGate.Wait(0))
                {
                    var obs = _observer;
                    var obsVal = next.Value;
                    Interlocked.Increment(ref _activeObservers);
                    var obsTask = Task.Run(() =>
                    {
                        try
                        {
                            obs.Report(obsVal);
                        }
                        catch
                        {
                        }
                        finally
                        {
                            try
                            {
                                _observerGate.Release();
                            }
                            catch (ObjectDisposedException)
                            {
                            }

                            Interlocked.Decrement(ref _activeObservers);
                        }
                    });
                    _ = obsTask.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
                }

                var seq = Interlocked.Increment(ref _sequence);
                var body = new ProgressBody(
                    _operationKey,
                    seq,
                    next.Value.Stage,
                    next.Value.Completed,
                    next.Value.Total,
                    next.Value.Unit,
                    next.Value.Message);

                await _sendAction(ProtocolMessages.CreateProgress(body), _stop.Token).ConfigureAwait(false);
                lastSent = _timeProvider.GetTimestamp();
                hasSent = true;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // 写帧异常时终止进度泵，不使业务计算抛出异常
                return;
            }
            finally
            {
                if (gateAcquired)
                {
                    _sendGate.Release();
                }
            }

            lock (_gate)
            {
                if (_pending.Count > 0)
                {
                    SignalPump();
                }
            }
        }
    }

    /// <summary>
    /// 待发送的阶段进度项。
    /// </summary>
    /// <param name="Generation">记录该进度时的尝试代次。</param>
    /// <param name="Value">进度模型实例。</param>
    private sealed record PendingProgress(long Generation, RunProgress Value);
}
