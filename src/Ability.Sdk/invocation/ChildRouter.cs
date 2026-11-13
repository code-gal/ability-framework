using System.Threading.Channels;
using Ability.Core;

namespace Ability.Sdk;

/// <summary>
/// 子调用路由与控制消息中继调度管理器，负责维护直接子调用与后代调用的路由映射，
/// 并通过独立有界调度通道向子管道异步中继宿主下发的控制消息。
/// </summary>
internal sealed class ChildRouter : IAsyncDisposable
{
    /// <summary>
    /// 子控制消息转发通道默认容量上限。
    /// </summary>
    private const int MaxPendingControlFrames = 64;

    /// <summary>
    /// 诊断记录使用的类别。
    /// </summary>
    private const string DiagnosticCategory = "Ability.Sdk.ChildRouter";

    /// <summary>
    /// 路由表登记与状态裁决互斥锁。
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// 直接子调用会话的控制委托与停止委托映射字典。
    /// </summary>
    private readonly Dictionary<string, (Func<Task> Stop, Func<IpcEnvelope, CancellationToken, Task> Control)> _childSessions = new(StringComparer.Ordinal);

    /// <summary>
    /// 后代调用键至直接子调用键的路由映射字典。
    /// </summary>
    private readonly Dictionary<string, string> _descendantRoutes = new(StringComparer.Ordinal);

    /// <summary>
    /// 子控制消息有界入队通道。
    /// </summary>
    private readonly Channel<ChildControlForward> _childControls = Channel.CreateBounded<ChildControlForward>(
        new BoundedChannelOptions(MaxPendingControlFrames)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    /// <summary>
    /// 限制子控制在途转发容量的名额信号量。
    /// </summary>
    private readonly SemaphoreSlim _childControlSlots = new(MaxPendingControlFrames, MaxPendingControlFrames);

    /// <summary>
    /// 驱动子控制转发循环的计数信号量。
    /// </summary>
    private readonly SemaphoreSlim _childControlSignal = new(0);

    /// <summary>
    /// 停止子控制转发循环的取消源。
    /// </summary>
    private readonly CancellationTokenSource _childControlShutdown = new();

    /// <summary>
    /// 子控制转发后台异步任务。
    /// </summary>
    private readonly Task _childControlTask;

    /// <summary>
    /// 活跃的控制转发生产者计数。
    /// </summary>
    private int _activeProducers;

    /// <summary>
    /// 活跃生产者归零的异步通知完成源。
    /// </summary>
    private readonly TaskCompletionSource _zeroProducersTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 单例共享释放任务。
    /// </summary>
    private Task? _disposeTask;

    /// <summary>
    /// 共享释放同步锁。
    /// </summary>
    private readonly Lock _disposeLock = new();

    /// <summary>
    /// 是否已被释放的标志位。
    /// </summary>
    private int _disposed;

    /// <summary>
    /// 子调用停止过程中是否出现失败的标志位。
    /// </summary>
    private int _hasFailedStops;

    /// <summary>
    /// 获取子调用停止过程中是否出现过任何失败。
    /// </summary>
    public bool HasFailedStops => Volatile.Read(ref _hasFailedStops) != 0;

    /// <summary>
    /// 初始化子调用路由与控制中继调度管理器实例，并启动后台转发循环。
    /// </summary>
    public ChildRouter()
    {
        _childControlTask = Task.Run(ChildControlLoopAsync);
    }

    /// <summary>
    /// 登记直接子调用会话的停止与控制委托。
    /// 若管理器已被释放或正在释放，返回 false 明确拒绝。
    /// </summary>
    /// <param name="invocationKey">子调用唯一标识键。</param>
    /// <param name="stop">异步停止与回收该子调用的委托。</param>
    /// <param name="control">向该子调用管道写出控制信封的委托。</param>
    /// <returns>登记成功返回 true；已关闭则返回 false。</returns>
    public bool RegisterChildSession(string invocationKey, Func<Task> stop, Func<IpcEnvelope, CancellationToken, Task> control)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationKey);
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentNullException.ThrowIfNull(control);

        lock (_lock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            _childSessions[invocationKey] = (stop, control);
            return true;
        }
    }

    /// <summary>
    /// 注销指定直接子调用会话，并联动清理所有经由此直接子调用转发的后代路由映射。
    /// </summary>
    /// <param name="invocationKey">子调用唯一标识键。</param>
    public void RemoveChildSession(string invocationKey)
    {
        if (string.IsNullOrWhiteSpace(invocationKey))
        {
            return;
        }

        lock (_lock)
        {
            _childSessions.Remove(invocationKey);

            var descendantKeys = _descendantRoutes
                .Where(pair => string.Equals(pair.Value, invocationKey, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToList();

            foreach (var key in descendantKeys)
            {
                _descendantRoutes.Remove(key);
            }
        }
    }

    /// <summary>
    /// 持久记录子调用停止失败事实，确保即使单次子调用在会话前抛错或回收未确认，
    /// 会话收尾仍能识别存在未确认回收的子进程，从而拒绝提前释放租约。
    /// </summary>
    /// <param name="invocationKey">子调用唯一标识键。</param>
    /// <param name="failure">引发停止失败的异常。</param>
    public void RecordStopFailure(string invocationKey, Exception failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationKey);
        ArgumentNullException.ThrowIfNull(failure);

        Interlocked.Exchange(ref _hasFailedStops, 1);
        AbilityDiagnostics.Log("Warning", DiagnosticCategory, 0,
            $"记录子调用停止失败（调用键：{invocationKey}）：{AbilityDiagnostics.ExceptionSummary(failure)}");
    }

    /// <summary>
    /// 登记后代子调用到直接子调用的路由映射。
    /// </summary>
    /// <param name="descendantKey">孙子或更深层后代调用的调用键。</param>
    /// <param name="directChildKey">承担中继网关角色的直接子调用键。</param>
    public void RegisterDescendantRoute(string descendantKey, string directChildKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descendantKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(directChildKey);

        lock (_lock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _descendantRoutes[descendantKey] = directChildKey;
        }
    }

    /// <summary>
    /// 移除后代子调用路由映射。
    /// </summary>
    /// <param name="descendantKey">后代调用的调用键。</param>
    public void RemoveDescendantRoute(string descendantKey)
    {
        if (string.IsNullOrWhiteSpace(descendantKey))
        {
            return;
        }

        lock (_lock)
        {
            _descendantRoutes.Remove(descendantKey);
        }
    }

    /// <summary>
    /// 将宿主下发的 ChildMessage 控制信封投递至子调用转发队列。
    /// 若目标是直接子调用则剥离外层信封；若目标是深层后代则保持封装以供下游继续寻址路由。
    /// 若找不到路由则安全丢弃；若转发队列满载，主动终止该子调用以消除背压积压，不阻塞根通信泵。
    /// </summary>
    /// <param name="message">包含 ChildMessageBody 的外层 IPC 信封。</param>
    public void ForwardChildControl(IpcEnvelope message)
    {
        var control = ProtocolMessages.ReadBody<ChildMessageBody>(message);
        Func<Task> stopDelegate;
        Func<IpcEnvelope, CancellationToken, Task> controlDelegate;
        IpcEnvelope messageToSend;

        lock (_lock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Interlocked.Increment(ref _activeProducers);

            if (_childSessions.TryGetValue(control.InvocationKey, out var directSession))
            {
                stopDelegate = directSession.Stop;
                controlDelegate = directSession.Control;
                messageToSend = control.Message;
            }
            else if (_descendantRoutes.TryGetValue(control.InvocationKey, out var directKey)
                && _childSessions.TryGetValue(directKey, out var gatewaySession))
            {
                stopDelegate = gatewaySession.Stop;
                controlDelegate = gatewaySession.Control;
                messageToSend = message;
            }
            else
            {
                var remaining = Interlocked.Decrement(ref _activeProducers);
                if (remaining == 0 && Volatile.Read(ref _disposed) != 0)
                {
                    _zeroProducersTcs.TrySetResult();
                }

                AbilityDiagnostics.Log("Warning", DiagnosticCategory, 0, $"收到无主子控制消息，调用键：{control.InvocationKey}");
                return;
            }
        }

        try
        {
            if (!_childControlSlots.Wait(0))
            {
                AbilityDiagnostics.Log(
                    "Error",
                    DiagnosticCategory,
                    0,
                    $"子控制转发队列满载（>{MaxPendingControlFrames} 条），拒绝转发并终止子调用：{control.InvocationKey}");

                _ = ObserveChildStopAsync(stopDelegate());
                return;
            }

            if (_childControls.Writer.TryWrite(new ChildControlForward(controlDelegate, messageToSend)))
            {
                _childControlSignal.Release();
            }
            else
            {
                _childControlSlots.Release();
            }
        }
        finally
        {
            var remaining = Interlocked.Decrement(ref _activeProducers);
            if (remaining == 0 && Volatile.Read(ref _disposed) != 0)
            {
                _zeroProducersTcs.TrySetResult();
            }
        }
    }

    /// <summary>
    /// 观察后台停止任务；停止执行者已记录失败，此处负责读取任务异常以完成观察。
    /// </summary>
    /// <param name="stopTask">停止异步任务。</param>
    private static async Task ObserveChildStopAsync(Task stopTask)
    {
        try
        {
            await stopTask;
        }
        catch
        {
        }
    }

    /// <summary>
    /// 单一后台子控制转发循环，按入队顺序逐条写出至对应子管道，施加超时并真正取消底层写任务。
    /// </summary>
    private async Task ChildControlLoopAsync()
    {
        while (!_childControlShutdown.IsCancellationRequested)
        {
            try
            {
                await _childControlSignal.WaitAsync(_childControlShutdown.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            while (_childControls.Reader.TryRead(out var forward))
            {
                try
                {
                    using var sendTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await forward.Control(forward.Message, sendTimeoutCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AbilityDiagnostics.Log("Warning", DiagnosticCategory, 0, $"子控制消息写出失败：{AbilityDiagnostics.ExceptionSummary(ex)}");
                }
                finally
                {
                    _childControlSlots.Release();
                }
            }
        }
    }

    /// <summary>
    /// 异步释放子调用路由管理器，使用外部统一收尾预算停止所有直接子调用，停止后台转发循环并清理队列。
    /// 并发调用时共享同一个底层的释放清理任务，避免重复或竞争释放。
    /// </summary>
    /// <param name="cancellationToken">统一收尾预算取消令牌。</param>
    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        Task disposeTask;
        lock (_disposeLock)
        {
            if (_disposeTask == null)
            {
                Interlocked.Exchange(ref _disposed, 1);
                _disposeTask = PerformDisposeAsync();
            }

            disposeTask = _disposeTask;
        }

        return new ValueTask(disposeTask.WaitAsync(cancellationToken));
    }

    /// <summary>
    /// 执行实际的释放与资源回收逻辑，确保无论何时调用均能可靠排空在途生产者、
    /// 调度停止所有子调用、发出转发循环终止信号并在后台延续中安全释放同步原语。
    /// </summary>
    private async Task PerformDisposeAsync()
    {
        List<Func<Task>> stops;
        lock (_lock)
        {
            stops = _childSessions.Values.Select(s => s.Stop).ToList();
            _childSessions.Clear();
            _descendantRoutes.Clear();
        }

        if (Volatile.Read(ref _activeProducers) == 0)
        {
            _zeroProducersTcs.TrySetResult();
        }

        try
        {
            await _zeroProducersTcs.Task.ConfigureAwait(false);
        }
        catch
        {
        }

        var stopTasks = new List<Task>(stops.Count);
        List<Exception>? stopErrors = null;
        foreach (var stop in stops)
        {
            try
            {
                stopTasks.Add(stop());
            }
            catch (Exception ex)
            {
                stopErrors ??= [];
                stopErrors.Add(ex);
            }
        }

        try
        {
            if (stopTasks.Count > 0)
            {
                await Task.WhenAll(stopTasks).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            stopErrors ??= [];
            stopErrors.Add(ex);
        }
        finally
        {
            _childControlShutdown.Cancel();
            try
            {
                _childControlSignal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        try
        {
            await _childControlTask.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            try { _childControlShutdown.Dispose(); } catch { }
            try { _childControlSlots.Dispose(); } catch { }
            try { _childControlSignal.Dispose(); } catch { }
        }

        if (stopErrors is { Count: > 0 })
        {
            Interlocked.Exchange(ref _hasFailedStops, 1);
            throw new AggregateException("子调用停止失败", stopErrors);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => DisposeAsync(CancellationToken.None);
}
