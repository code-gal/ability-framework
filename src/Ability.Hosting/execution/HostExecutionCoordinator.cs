using System.Text.Json;
using System.Threading.Channels;
using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// 宿主端执行协调器，统筹主双工通信连接上的消息分发、流消费、子调用管理与治理策略调度。
/// 它同时是本次执行状态的唯一所有者：主循环按帧顺序写入终态、故障、输出与流关闭事实；
/// 异常分类只在终态仍为 <c>Unknown</c> 时补判，收尾步骤只释放工作者，不改写执行状态；
/// 后台工作者只读取这些事实（如流消费读取输出类型），不取得原先没有的写入权。
/// </summary>
/// <param name="connection">已完成握手的底层双工 IPC 连接。</param>
/// <param name="registry">权威调用登记表。</param>
/// <param name="rootRecord">本次任务的根调用记录。</param>
/// <param name="childCalls">子调用协调器。</param>
/// <param name="governance">治理协调器。</param>
/// <param name="options">宿主运行选项配置。</param>
/// <param name="invocation">任务调用定义。</param>
/// <param name="callbackDispatcher">宿主回调隔离派发器。</param>
/// <param name="callerToken">调用方外部取消令牌。</param>
/// <param name="executionCts">执行超时取消令牌源。</param>
/// <param name="sessionToken">任务生命周期取消令牌。</param>
internal sealed partial class HostExecutionCoordinator(
    IpcConnection connection,
    InvocationRegistry registry,
    InvocationRecord rootRecord,
    HostChildCalls childCalls,
    HostGovernanceCoordinator governance,
    HostRuntimeOptions options,
    AbilityInvocation invocation,
    HostCallbackDispatcher callbackDispatcher,
    CancellationToken callerToken,
    CancellationTokenSource executionCts,
    CancellationToken sessionToken)
{
    /// <summary>记录的已观测消息类型最大条数。</summary>
    private const int MaxObservedMessages = 128;
    /// <summary>默认外部消费与回调超时时限。</summary>
    private static readonly TimeSpan DefaultCallbackTimeout = TimeSpan.FromSeconds(30);
    /// <summary>默认控制消息出站写出超时时限。</summary>
    private static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(2);
    /// <summary>后台任务容量上限（64）。</summary>
    private const int MaxBackgroundTasks = 64;
    /// <summary>协议中表示受管操作无响应的通知类型；Core 共享常量表尚未收录该通知。</summary>
    private const string OperationUnresponsiveType = "OperationUnresponsive";

    /// <summary>任务全局治理请求调度唯一编号生成器。</summary>
    private long _nextGovernanceRequestId = 1;

    /// <summary>已观测到的协议消息类型列表。</summary>
    private readonly List<string> _observedMessages = new(16);

    /// <summary>观测消息类型列表是否因超限被截断。</summary>
    private bool _observedTruncated;

    /// <summary>协调器内部断连取消令牌源。</summary>
    private readonly CancellationTokenSource _disconnectCts = new();

    /// <summary>记录的受管断连故障分类。</summary>
    private string? _faultDisconnectKind;

    /// <summary>记录的受管断连底层异常。</summary>
    private Exception? _faultDisconnectError;

    /// <summary>受管断连关闭底层连接任务引用。</summary>
    private Task? _faultCloseTask;

    /// <summary>断连状态操作同步锁。</summary>
    private readonly Lock _disconnectLock = new();

    /// <summary>本次执行终态标识，主循环写入、异常分类补判。</summary>
    private string _outcome = "Unknown";

    /// <summary>已接受的技能业务终态。</summary>
    private string? _businessOutcome;

    /// <summary>结果输出类型名；流消费回调按此投影结果项类型。</summary>
    private string? _outputTypeName;

    /// <summary>一元结果输出元素。</summary>
    private JsonElement? _outputJson;

    /// <summary>已接收的流元素计数。</summary>
    private int _streamItemCount;

    /// <summary>故障种类。</summary>
    private string? _faultKind;

    /// <summary>故障说明。</summary>
    private string? _faultMessage;

    /// <summary>结果协议是否已进入流关闭对账阶段；只在主循环内读写。</summary>
    private bool _isStreamClosing;

    /// <summary>流关闭排空等待的取消源；收尾释放。</summary>
    private CancellationTokenSource? _streamCloseTimeoutCts;

    /// <summary>流关闭排空等待任务；只在主循环与收尾观测。</summary>
    private Task? _streamCloseTimeoutTask;

    /// <summary>关联会话注销与内部断连的执行期取消令牌。</summary>
    private CancellationToken _activeSessionToken;

    /// <summary>结果项有界写入通道；主循环写入、流消费者读取。</summary>
    private Channel<ResultItemBody>? _resultItemChannel;

    /// <summary>流式结果消费者。</summary>
    private HostStreamConsumer? _streamConsumer;

    /// <summary>流元素消费工作循环任务。</summary>
    private Task? _resultConsumerTask;

    /// <summary>流消费工作循环的取消源；由收尾按使用结束释放。</summary>
    private CancellationTokenSource? _resultConsumerCts;

    /// <summary>策略更新工作循环任务。</summary>
    private Task? _policyUpdateTask;

    /// <summary>策略更新工作循环的取消源；由收尾按使用结束释放。</summary>
    private CancellationTokenSource? _policyUpdateCts;

    /// <summary>
    /// 运行消息消费主循环直至终态响应或取消，返回本次执行的结果快照。
    /// 工作者准备与消息循环同处一个 <c>try</c> 保护内；收尾只释放工作者与登记物理退出；
    /// 收尾之后仍按未定终态补判断连与取消分类，再构造结果。
    /// </summary>
    /// <returns>执行产出快照。</returns>
    public async Task<HostExecutionResult> ExecuteAsync()
    {
        _outputTypeName = invocation.OutputTypeName;

        using var coordinatorLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, _disconnectCts.Token);
        _activeSessionToken = coordinatorLinkedCts.Token;

        try
        {
            PrepareWorkers();
            await RunMessageLoopAsync().ConfigureAwait(false);
        }
        catch (ProtocolException protocolError)
        {
            ClassifyProtocolFailure(protocolError);
        }
        catch (OperationCanceledException)
        {
            ClassifyCancellation();
        }
        catch (Exception executionError)
        {
            ClassifyUnexpectedFailure(executionError);
        }
        finally
        {
            await ReleaseWorkersAsync().ConfigureAwait(false);
        }

        ClassifyUnknownTerminal();
        return BuildExecutionResult();
    }

    /// <summary>
    /// 准备策略更新泵与流式结果消费管道及其工作循环。
    /// 策略更新在未配置通道时不启动；流消费循环在本次执行全程唯一。
    /// </summary>
    private void PrepareWorkers()
    {
        if (options.PolicyUpdates is not null)
        {
            _policyUpdateCts = new CancellationTokenSource();
            var policyToken = _policyUpdateCts.Token;
            var policyUpdates = options.PolicyUpdates;
            _policyUpdateTask = Task.Run(() => RunPolicyUpdatesAsync(policyUpdates, policyToken));
        }

        _resultItemChannel = Channel.CreateBounded<ResultItemBody>(new BoundedChannelOptions(options.ResultCreditWindow)
        {
            SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait
        });
        _resultConsumerCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        _streamConsumer = new HostStreamConsumer(
            _resultItemChannel.Reader,
            connection,
            options,
            () => _outputTypeName,
            DefaultSendTimeout,
            _resultConsumerCts.Token);
        _resultConsumerTask = _streamConsumer.RunLoopAsync();
    }

    /// <summary>
    /// 消费入站消息直至终态响应、取消或连接关闭。
    /// 循环只做等待裁决、帧读取与处置分派；每帧的具体处理按结果协议、子调用、治理与观测分组执行。
    /// </summary>
    /// <returns>消息循环的异步完成任务。</returns>
    private async Task RunMessageLoopAsync()
    {
        while (true)
        {
            var waitReadTask = connection.Inbound.WaitToReadAsync(_activeSessionToken).AsTask();
            var completedTask = _isStreamClosing
                ? await Task.WhenAny(waitReadTask, _resultConsumerTask!, _streamCloseTimeoutTask!).ConfigureAwait(false)
                : await Task.WhenAny(waitReadTask, _resultConsumerTask!).ConfigureAwait(false);

            if (completedTask == _resultConsumerTask && await ResolveConsumerCompletionAsync().ConfigureAwait(false))
            {
                break;
            }

            if (_isStreamClosing && completedTask == _streamCloseTimeoutTask)
            {
                ResolveStreamCloseTimeout();
                break;
            }

            if (!await waitReadTask.ConfigureAwait(false))
            {
                break;
            }

            if (!connection.Inbound.TryRead(out var envelope))
            {
                continue;
            }

            if (!await HandleInboundEnvelopeAsync(envelope).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    /// <summary>
    /// 裁决流消费工作循环先于入站消息结束时的终态。
    /// 消费任务故障或取消时统一裁决为消费故障并结束循环；流关闭阶段消费正常结束时完成关闭对账。
    /// </summary>
    /// <returns>需要结束主循环时为 true。</returns>
    private async Task<bool> ResolveConsumerCompletionAsync()
    {
        if (_resultConsumerTask!.IsFaulted || _resultConsumerTask.IsCanceled)
        {
            var (resolvedOutcome, resolvedKind, resolvedMessage) =
                ResolveCancellationOutcome(_faultKind, _faultMessage, _resultConsumerTask);
            _outcome = resolvedOutcome;
            _faultKind = resolvedKind;
            _faultMessage = resolvedMessage;
            return true;
        }

        if (!_isStreamClosing)
        {
            return false;
        }

        await CompleteResultCloseAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 以本地累计消费项数向技能端回发 <c>ResultClosed</c> 完成对账，并记录流结果终态。
    /// </summary>
    /// <returns>对账写出的异步完成任务。</returns>
    private async Task CompleteResultCloseAsync()
    {
        var count = (int)connection.StreamState.ItemCount;
        using var sendCts = new CancellationTokenSource(DefaultSendTimeout, options.TimeProvider);
        using var linkedSendCts = CancellationTokenSource.CreateLinkedTokenSource(sendCts.Token, sessionToken);
        await connection.SendAsync(ProtocolMessages.CreateResultClosed(count), linkedSendCts.Token).ConfigureAwait(false);
        connection.StreamState.MarkClosed(count);

        _streamItemCount = count;
        _outcome = "ResultClose";
        _businessOutcome = "ResultClose";
    }

    /// <summary>
    /// 记录流关闭排空超时的消费故障终态；业务终态保持已声明的流式关闭。
    /// </summary>
    private void ResolveStreamCloseTimeout()
    {
        _outcome = "ConsumerFault";
        _businessOutcome = "ResultClose";
        _faultKind = "ResultCloseTimeout";
        _faultMessage = "流元素消费排空超时。";
    }

    /// <summary>
    /// 记录协议异常对应的终态分类；已由处理步骤判定的终态与故障不被覆盖。
    /// </summary>
    /// <param name="protocolError">协议异常。</param>
    private void ClassifyProtocolFailure(ProtocolException protocolError)
    {
        if (string.Equals(_outcome, "Unknown", StringComparison.Ordinal))
        {
            _outcome = "ProtocolError";
        }

        _faultKind ??= "ProtocolViolation";
        _faultMessage ??= protocolError.Message;
    }

    /// <summary>
    /// 取消导致的循环退出按受管断连、操作无响应、调用方取消、执行超时与消费故障顺序分类。
    /// </summary>
    private void ClassifyCancellation()
    {
        if (!string.Equals(_outcome, "Unknown", StringComparison.Ordinal))
        {
            return;
        }

        if (_disconnectCts.IsCancellationRequested)
        {
            _outcome = "Disconnected";
            _faultKind = _faultDisconnectKind ?? "TransportFault";
            _faultMessage = "必要答复或通知确认传输失败，已执行受管断连。";
            return;
        }

        var (resolvedOutcome, resolvedKind, resolvedMessage) =
            ResolveCancellationOutcome(_faultKind, _faultMessage, _resultConsumerTask);
        _outcome = resolvedOutcome;
        _faultKind = resolvedKind;
        _faultMessage = resolvedMessage;
    }

    /// <summary>
    /// 通信或运行时未预期异常对应的终态分类；已判定的终态不被覆盖，故障原因保留原异常类型。
    /// </summary>
    /// <param name="executionError">未预期异常。</param>
    private void ClassifyUnexpectedFailure(Exception executionError)
    {
        if (!string.Equals(_outcome, "Unknown", StringComparison.Ordinal))
        {
            return;
        }

        _outcome = "Disconnected";
        _faultKind ??= _faultDisconnectKind ?? executionError.GetType().Name;
        _faultMessage = "通信或运行时发生异常。";
    }

    /// <summary>
    /// 收尾之后对仍未知的终态补判：断连事实优先，其次按调用方取消、执行期限与会话注销分类。
    /// 收尾期间发生的状态变化同样参与判定，不把终态判定全部提前到收尾之前。
    /// </summary>
    private void ClassifyUnknownTerminal()
    {
        if (!string.Equals(_outcome, "Unknown", StringComparison.Ordinal))
        {
            return;
        }

        if (_disconnectCts.IsCancellationRequested)
        {
            _outcome = "Disconnected";
            _faultKind = _faultDisconnectKind ?? "TransportFault";
            _faultMessage = "必要答复或通知确认传输失败，已执行受管断连。";
            return;
        }

        if (callerToken.IsCancellationRequested || executionCts.IsCancellationRequested || sessionToken.IsCancellationRequested)
        {
            var (resolvedOutcome, resolvedKind, resolvedMessage) =
                ResolveCancellationOutcome(_faultKind, _faultMessage, _resultConsumerTask);
            _outcome = resolvedOutcome;
            _faultKind = resolvedKind;
            _faultMessage = resolvedMessage;
        }
    }

    /// <summary>
    /// 构造前台执行结果快照：流元素数取连接流状态的实际计数，观测消息列表按原引用传出。
    /// </summary>
    /// <returns>本次执行的结果快照。</returns>
    private HostExecutionResult BuildExecutionResult()
    {
        if (connection is not null)
        {
            _streamItemCount = (int)connection.StreamState.ItemCount;
        }

        return new HostExecutionResult
        {
            Outcome = _outcome,
            BusinessOutcome = _businessOutcome,
            OutputTypeName = _outputTypeName,
            OutputJson = _outputJson,
            StreamItemCount = _streamItemCount,
            FaultKind = _faultKind,
            FaultMessage = _faultMessage,
            ObservedMessages = _observedMessages,
            ObservedTruncated = _observedTruncated
        };
    }

    /// <summary>
    /// 释放一步后台工作循环时产生的两个延续任务：请求取消的任务，以及在其后释放取消源的任务。
    /// 两者都由协调器持有并登记进物理退出追踪；取消源释放严格晚于实际使用者退出。
    /// </summary>
    /// <param name="CancelTask">请求取消工作循环的任务；未安排取消时为 null。</param>
    /// <param name="DisposeTask">等待实际使用者退出后释放取消源的延续任务。</param>
    private readonly record struct WorkerReleaseContinuations(Task? CancelTask, Task? DisposeTask);

    /// <summary>
    /// 收尾释放本次执行创建的工作者：先结束流关闭等待，再按使用结束取消并释放消费与策略取消源，
    /// 随后排空协调器内部后台任务，最后登记完全物理退出追踪。
    /// 收尾不改写执行状态，只决定各取消源何时可以释放。
    /// </summary>
    /// <returns>收尾释放的异步完成任务。</returns>
    private async Task ReleaseWorkersAsync()
    {
        await ReleaseStreamCloseWaitAsync().ConfigureAwait(false);

        var consumerRelease = await ReleaseResultConsumerAsync().ConfigureAwait(false);
        var policyRelease = await ReleasePolicyUpdatesAsync().ConfigureAwait(false);

        await DrainBackgroundTasksAsync().ConfigureAwait(false);
        TrackPhysicalExit(
            _policyUpdateTask,
            _resultConsumerTask,
            _streamConsumer?.PhysicalExitTask,
            consumerRelease.CancelTask,
            consumerRelease.DisposeTask,
            policyRelease.CancelTask,
            policyRelease.DisposeTask);
    }

    /// <summary>
    /// 结束唯一的流关闭排空等待：取消计时器、观察其退出后释放取消源。
    /// 未进入流关闭阶段时不创建也不释放任何等待。
    /// </summary>
    /// <returns>结束等待的异步完成任务。</returns>
    private async Task ReleaseStreamCloseWaitAsync()
    {
        if (_streamCloseTimeoutCts is null)
        {
            return;
        }

        _streamCloseTimeoutCts.Cancel();
        try
        {
            await _streamCloseTimeoutTask!.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_streamCloseTimeoutCts.IsCancellationRequested)
        {
            // ResultClose 已完成或主循环退出，结束唯一的关闭等待。
        }
        finally
        {
            _streamCloseTimeoutCts.Dispose();
        }
    }

    /// <summary>
    /// 结束流消费工作：关闭写入端，非正常结果终态时请求取消消费循环，
    /// 在有限预算内观察消费任务，并在其实际退出后释放消费取消源。
    /// </summary>
    /// <returns>本轮产生的取消任务与取消源释放延续任务。</returns>
    private async Task<WorkerReleaseContinuations> ReleaseResultConsumerAsync()
    {
        _resultItemChannel?.Writer.TryComplete();

        if (_resultConsumerTask is null)
        {
            DisposeWorkerCancellationSource(_resultConsumerCts, "ConsumerDispose");
            return default;
        }

        Task? cancelTask = null;
        if (_outcome is not ("SubmitResult" or "ResultClose") && _resultConsumerCts is not null)
        {
            var consumerCts = _resultConsumerCts;
            cancelTask = Task.Run(() =>
            {
                try { consumerCts.Cancel(); }
                catch (Exception ex) { callbackDispatcher.RecordIssue("ConsumerCancel", ex.GetType().Name, DescribeSafeError(ex)); }
            });
        }

        var drainTasks = cancelTask is not null ? new[] { _resultConsumerTask, cancelTask } : new[] { _resultConsumerTask };
        using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(2), options.TimeProvider);
        try { await Task.WhenAll(drainTasks).WaitAsync(drainCts.Token).ConfigureAwait(false); } catch { }
        var disposeTask = Task.WhenAll(drainTasks).ContinueWith(static (drained, state) =>
        {
            _ = drained.Exception;
            var (cts, dispatcher, owner) = ((CancellationTokenSource?, HostCallbackDispatcher, HostExecutionCoordinator))state!;
            try { cts?.Dispose(); }
            catch (Exception ex) { owner.NoteReleaseFailure("ConsumerDispose", ex, dispatcher); }
        }, (_resultConsumerCts, callbackDispatcher, this), TaskScheduler.Default);

        return new WorkerReleaseContinuations(cancelTask, disposeTask);
    }

    /// <summary>
    /// 结束策略更新工作：请求取消策略泵，在 1 秒预算内观察其退出，
    /// 并在其实际退出后释放策略取消源；收尾异常只记诊断并按原分类触发受管断连。
    /// </summary>
    /// <returns>本轮产生的取消任务与取消源释放延续任务。</returns>
    private async Task<WorkerReleaseContinuations> ReleasePolicyUpdatesAsync()
    {
        if (_policyUpdateTask is null)
        {
            DisposeWorkerCancellationSource(_policyUpdateCts, "PolicyDispose");
            return default;
        }

        Task? cancelTask = null;
        if (_policyUpdateCts is not null)
        {
            var policyCts = _policyUpdateCts;
            cancelTask = Task.Run(() =>
            {
                try { policyCts.Cancel(); }
                catch (Exception ex) { callbackDispatcher.RecordIssue("PolicyCancel", ex.GetType().Name, DescribeSafeError(ex)); }
            });
        }

        var drainTasks = cancelTask is not null ? new[] { _policyUpdateTask, cancelTask } : new[] { _policyUpdateTask };
        var disposeTask = Task.WhenAll(drainTasks).ContinueWith(static (drained, state) =>
        {
            _ = drained.Exception;
            var (cts, dispatcher, owner) = ((CancellationTokenSource?, HostCallbackDispatcher, HostExecutionCoordinator))state!;
            try { cts?.Dispose(); }
            catch (Exception ex) { owner.NoteReleaseFailure("PolicyDispose", ex, dispatcher); }
        }, (_policyUpdateCts, callbackDispatcher, this), TaskScheduler.Default);

        try
        {
            await Task.WhenAll(drainTasks).WaitAsync(TimeSpan.FromSeconds(1), options.TimeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            callbackDispatcher.DispatchDiagnostic("策略更新任务在退出时未能在 1 秒内完全结束。");
        }
        catch (OperationCanceledException) when (_policyUpdateCts?.IsCancellationRequested == true || sessionToken.IsCancellationRequested)
        {
            // 会话收尾已主动取消策略工作循环。
        }
        catch (Exception ex)
        {
            callbackDispatcher.DispatchDiagnostic($"策略更新任务收尾异常: {DescribeSafeError(ex)}");
            TriggerFaultDisconnect("PolicyUpdateLoopFault", ex);
        }

        return new WorkerReleaseContinuations(cancelTask, disposeTask);
    }

    /// <summary>
    /// 释放未被工作循环使用的取消源；释放异常按事实记录并置位释放失败。
    /// </summary>
    /// <param name="cts">待释放的取消源；为 null 时不操作。</param>
    /// <param name="stage">释放阶段名称。</param>
    private void DisposeWorkerCancellationSource(CancellationTokenSource? cts, string stage)
    {
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Dispose();
        }
        catch (Exception ex)
        {
            NoteReleaseFailure(stage, ex, callbackDispatcher);
        }
    }

    /// <summary>释放阶段是否发生过异常，由宿主收尾按事实读取。</summary>
    private volatile bool _releaseFailed;

    /// <summary>获取执行协调器释放阶段是否发生过异常。</summary>
    public bool HasReleaseFailure => _releaseFailed;

    /// <summary>
    /// 记录释放阶段异常并置位释放失败事实；问题记录到共用派发器的收尾问题列表。
    /// </summary>
    /// <param name="stage">发生阶段名称。</param>
    /// <param name="failure">释放阶段异常。</param>
    /// <param name="recorder">收尾问题记录者。</param>
    private void NoteReleaseFailure(string stage, Exception failure, HostCallbackDispatcher recorder)
    {
        _releaseFailed = true;
        recorder.RecordIssue(stage, failure.GetType().Name, DescribeSafeError(failure));
    }
}
