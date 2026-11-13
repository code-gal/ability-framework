using Ability.Core;
using Ability.Model;

namespace Ability.Sdk;

/// <summary>
/// 一次已由宿主确认并受运行策略管理的业务操作资源。
/// 操作管理内部尝试生命周期（单次尝试对应一次 ExecuteAsync 调用）；
/// 负责单次尝试的局部超时监测、尝试次数上限校验、进度通道协调、动态策略应用及到期与无响应收尾。
/// </summary>
public sealed partial class ManagedOperation : IAsyncDisposable
{
    /// <summary>
    /// 底层出站协议帧发送委托。
    /// </summary>
    private readonly Func<IpcEnvelope, CancellationToken, Task> _sendAction;

    /// <summary>
    /// 从外部会话活跃操作表移除自身的通知动作。
    /// </summary>
    private readonly Action<string> _removeOperationAction;

    /// <summary>
    /// 操作登记时绑定的根取消令牌。
    /// </summary>
    private readonly CancellationToken _rootCancellation;

    /// <summary>
    /// 会话级全局取消令牌。
    /// </summary>
    private readonly CancellationToken _sessionCancellation;

    /// <summary>
    /// 时间提供者。
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 尝试取消与清理时的安全等待宽限期。
    /// </summary>
    private readonly TimeSpan _cleanupGrace;

    /// <summary>
    /// 确保尝试串行执行的单并发互斥信号量。
    /// </summary>
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    /// <summary>
    /// 结构化进度有界限频合并通道。
    /// </summary>
    private readonly ProgressChannel _progress;

    /// <summary>
    /// 保护动态策略更新状态互斥锁。
    /// </summary>
    private readonly object _policyGate = new();

    /// <summary>
    /// 当前生效的运行策略模型。
    /// </summary>
    private RunPolicyMdl _policy;

    /// <summary>
    /// 动态策略变更通知信号源。
    /// </summary>
    private TaskCompletionSource _policyChanged = NewSignal();

    /// <summary>
    /// 当前生效策略的版本号。
    /// </summary>
    private long _policyVersion;

    /// <summary>
    /// 当前生效策略的来源说明。
    /// </summary>
    private string _policySource;

    /// <summary>
    /// 策略最近一次变更的高精度单调时间戳。
    /// </summary>
    private long _updatedAt;

    /// <summary>
    /// 累计已发起的尝试次数。
    /// </summary>
    private int _attempts;

    /// <summary>
    /// 当前尝试状态（0: 未开始, 1: 运行中, 2: 已终态）。
    /// </summary>
    private int _attemptState;

    /// <summary>
    /// 当前活跃尝试的序号。
    /// </summary>
    private int _activeAttempt;

    /// <summary>
    /// 当前尝试的局部取消令牌源。
    /// </summary>
    private CancellationTokenSource? _activeCancellation;

    /// <summary>
    /// 当前活跃尝试的后台取消执行任务（统一持有取消所有权）。
    /// </summary>
    private Task? _activeAttemptCancelTask;

    /// <summary>
    /// 当前尝试业务完成同步等待源。
    /// </summary>
    private TaskCompletionSource? _activeCompletion;

    /// <summary>
    /// 期限是否已到期超时标记（0 未超时，1 已超时）。
    /// </summary>
    private int _expired;

    /// <summary>
    /// 当前尝试是否已关闭裁决标记（受 _policyGate 保护）。
    /// </summary>
    private bool _attemptClosed;

    /// <summary>
    /// 向会话登记尝试物理退出任务的委托。
    /// </summary>
    private readonly Action<Task>? _trackAttemptExitAction;

    /// <summary>
    /// 操作是否已整体结束标记（0 未结束，1 已结束）。
    /// </summary>
    private int _ended;

    /// <summary>
    /// 资源销毁是否已发起标记（0 未发起，1 已发起）。
    /// </summary>
    private int _disposeRequested;

    /// <summary>
    /// 资源销毁是否已彻底完成标记（0 未完成，1 已完成）。
    /// </summary>
    private int _disposeCompleted;

    /// <summary>
    /// 初始化受管操作。
    /// </summary>
    /// <param name="sendAction">发送协议帧的委托。</param>
    /// <param name="removeOperationAction">注销操作的委托。</param>
    /// <param name="operationKey">操作全局唯一标识键。</param>
    /// <param name="name">操作名称。</param>
    /// <param name="purpose">操作目标用途。</param>
    /// <param name="initialPolicy">生效的初始策略模型。</param>
    /// <param name="policyVersion">初始策略版本号。</param>
    /// <param name="policySource">初始策略来源。</param>
    /// <param name="rootCancellation">根任务调用方取消令牌。</param>
    /// <param name="sessionCancellation">会话连接级取消令牌。</param>
    /// <param name="timeProvider">时间提供者。</param>
    /// <param name="cleanupGrace">取消与退出等待宽限期。</param>
    /// <param name="observer">本地进度观察者（可选）。</param>
    /// <param name="trackAttemptExitAction">向会话登记尝试物理退出任务的委托（可选）。</param>
    internal ManagedOperation(
        Func<IpcEnvelope, CancellationToken, Task> sendAction,
        Action<string> removeOperationAction,
        string operationKey,
        string name,
        string purpose,
        RunPolicyMdl initialPolicy,
        long policyVersion,
        string policySource,
        CancellationToken rootCancellation,
        CancellationToken sessionCancellation,
        TimeProvider timeProvider,
        TimeSpan cleanupGrace,
        IProgress<RunProgress>? observer = null,
        Action<Task>? trackAttemptExitAction = null)
    {
        _sendAction = sendAction ?? throw new ArgumentNullException(nameof(sendAction));
        _removeOperationAction = removeOperationAction ?? throw new ArgumentNullException(nameof(removeOperationAction));
        OperationKey = operationKey ?? throw new ArgumentNullException(nameof(operationKey));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Purpose = purpose ?? throw new ArgumentNullException(nameof(purpose));
        _policy = initialPolicy ?? throw new ArgumentNullException(nameof(initialPolicy));
        _policyVersion = policyVersion;
        _policySource = policySource ?? "SkillSuggested";
        _rootCancellation = rootCancellation;
        _sessionCancellation = sessionCancellation;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cleanupGrace = cleanupGrace > TimeSpan.Zero ? cleanupGrace : TimeSpan.FromSeconds(1);
        _trackAttemptExitAction = trackAttemptExitAction;

        _updatedAt = _timeProvider.GetTimestamp();
        _progress = new ProgressChannel(
            sendAction,
            operationKey,
            initialPolicy.ProgressInterval,
            _timeProvider,
            observer,
            () =>
            {
                lock (_policyGate)
                {
                    return _ended == 0 && _attemptState == 1 ? _activeAttempt : 0;
                }
            });
    }

    /// <summary>
    /// 操作协议信封的默认发送超时时限。
    /// </summary>
    private static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 向宿主异步发送协议信封，应用默认有限时限。
    /// </summary>
    /// <param name="envelope">待发送的协议信封。</param>
    /// <param name="cancellationToken">可选取消令牌。</param>
    private async Task SendEnvelopeAsync(IpcEnvelope envelope, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(DefaultSendTimeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
        try
        {
            await _sendAction(envelope, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"发送协议信封 {envelope.Type} 超过时限 {DefaultSendTimeout}。");
        }
    }

    /// <summary>
    /// 采用共同顺序裁决写出尝试结束信封。
    /// 清空未发出的残留进度并在排他门控下发送，保证旧尝试进度绝不越过 AttemptEnded。
    /// </summary>
    /// <param name="envelope">尝试结束协议信封。</param>
    private async Task SendAttemptEndedEnvelopeAsync(IpcEnvelope envelope)
    {
        try
        {
            await _progress.SendAttemptEndedAsync(() => SendEnvelopeAsync(envelope)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 等待进度门控超时，无法安全排序尝试结束信封，标记操作终止并通知无响应
            Interlocked.Exchange(ref _ended, 1);
            TryCompleteAttempt();
            try
            {
                await SendEnvelopeAsync(ProtocolMessages.CreateOperationState(
                    "OperationUnresponsive", OperationKey, Attempts, "Unresponsive", "尝试结束信封等待进度通道门控超时")).ConfigureAwait(false);
            }
            catch { }
            throw;
        }
    }

    /// <summary>
    /// 操作的唯一标识键。
    /// </summary>
    public string OperationKey { get; }

    /// <summary>
    /// 操作人类可读名称。
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 操作目标用途说明。
    /// </summary>
    public string Purpose { get; }

    /// <summary>
    /// 已发起的累计尝试次数。
    /// </summary>
    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>
    /// 当前生效策略的版本号。
    /// </summary>
    public long PolicyVersion
    {
        get
        {
            lock (_policyGate)
            {
                return _policyVersion;
            }
        }
    }

    /// <summary>
    /// 当前生效策略的来源说明。
    /// </summary>
    public string PolicySource
    {
        get
        {
            lock (_policyGate)
            {
                return _policySource;
            }
        }
    }

    /// <summary>
    /// 仲裁并应用来自宿主的动态策略更新。
    /// 返回同一次裁决的结果状态与生效版本号，避免二次读取状态产生版本竞态。
    /// </summary>
    /// <param name="policyBody">新策略体。</param>
    /// <param name="version">新策略版本号。</param>
    /// <param name="source">策略来源。</param>
    /// <returns>策略应用裁决结果元组（Outcome, Version）。</returns>
    internal (string Outcome, long Version) ApplyPolicy(RunPolicyBody policyBody, long version, string source)
    {
        var model = RunPolicyMdl.FromBody(policyBody);
        lock (_policyGate)
        {
            if (_ended != 0)
            {
                return ("Ended", version);
            }

            if (_attemptState == 2)
            {
                return (_expired != 0 ? "Expired" : "Stopped", version);
            }

            if (version <= _policyVersion)
            {
                return ("Stale", version);
            }

            _policy = model;
            _policyVersion = version;
            _policySource = source;
            _updatedAt = _timeProvider.GetTimestamp();
            _expired = 0;
            _progress.UpdateInterval(model.ProgressInterval);

            var signal = _policyChanged;
            _policyChanged = NewSignal();
            signal.TrySetResult();

            return ("Applied", version);
        }
    }

    /// <summary>
    /// 执行一次受管尝试。每次调用独立创建局部取消令牌与期限守卫，在累计重试上限内允许再次尝试。
    /// </summary>
    /// <typeparam name="TResult">操作返回值类型。</typeparam>
    /// <param name="handler">尝试业务处理委托。</param>
    /// <param name="cancellationToken">本次尝试的外部取消令牌（可选）。</param>
    /// <returns>业务执行返回结果。</returns>
    public async Task<TResult> ExecuteAsync<TResult>(
        Func<ManagedOperationContext, Task<TResult>> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _ended) != 0, this);

        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _ended) != 0, this);

            CancellationTokenSource? attemptCancellation = null;
            CancellationTokenSource? deadlineStop = null;
            TaskCompletionSource? attemptExitTcs = null;
            int attempt;
            long startedAt;
            var registrationStep = 0;

            try
            {
                attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, _rootCancellation, _sessionCancellation);
                deadlineStop = CancellationTokenSource.CreateLinkedTokenSource(attemptCancellation.Token);
                attemptExitTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                startedAt = _timeProvider.GetTimestamp();

                lock (_policyGate)
                {
                    // 尝试启动登记与操作释放竞争同一次同步裁决：释放胜出则立即明确拒绝
                    ObjectDisposedException.ThrowIf(_ended != 0, this);

                    var currentPolicy = _policy;
                    var maxAttempts = checked(currentPolicy.MaxRetries + 1);
                    var nextAttempt = _attempts + 1;
                    if (nextAttempt > maxAttempts)
                    {
                        throw new InvalidOperationException($"操作 '{Name}' 已达到累计尝试上限 {maxAttempts}（重试上限 {currentPolicy.MaxRetries}）。");
                    }

                    var completionSignal = NewSignal();

                    // 步骤 1：递增累计尝试计数
                    _attempts = nextAttempt;
                    attempt = nextAttempt;
                    registrationStep = 1;

                    // 步骤 2：发布活跃尝试状态
                    _attemptClosed = false; _attemptState = 1;
                    _activeAttempt = attempt; _activeCancellation = attemptCancellation;
                    _activeAttemptCancelTask = null; _activeCompletion = completionSignal;
                    _updatedAt = startedAt; _expired = 0;
                    registrationStep = 2;

                    // 步骤 3：登记退出任务至会话追踪
                    _trackAttemptExitAction?.Invoke(attemptExitTcs.Task);
                    registrationStep = 3;
                }
            }
            catch
            {
                // 根据本次实际完成的登记步骤决定回滚：发布前失败不改变计数与状态；登记中失败仅撤销已完成状态
                Task? cancelTask = null;
                if (registrationStep > 0)
                {
                    lock (_policyGate)
                    {
                        if (registrationStep >= 2)
                        {
                            _attemptClosed = true; _attemptState = 0; _activeCancellation = null;
                            cancelTask = _activeAttemptCancelTask;
                            _activeCompletion?.TrySetResult(); _activeCompletion = null;
                        }
                        if (registrationStep >= 1) { _attempts--; }
                    }
                }

                if (cancelTask is not null)
                {
                    _ = cancelTask.ContinueWith(static (t, state) =>
                    {
                        _ = t.Exception;
                        var (attCts, dlCts, exitTcs) = ((CancellationTokenSource?, CancellationTokenSource?, TaskCompletionSource?))state!;
                        try { attCts?.Dispose(); } catch { }
                        try { dlCts?.Dispose(); } catch { }
                        exitTcs?.TrySetResult();
                    }, (attemptCancellation, deadlineStop, attemptExitTcs), TaskScheduler.Default);
                }
                else
                {
                    try { attemptCancellation?.Dispose(); } catch { }
                    try { deadlineStop?.Dispose(); } catch { }
                    attemptExitTcs?.TrySetResult();
                }

                throw;
            }

            var terminalSent = false;
            Task<TResult>? handlerTask = null;
            Task<bool>? deadlineTask = null;

            try
            {
                // 发送尝试开始通知
                await SendEnvelopeAsync(ProtocolMessages.CreateOperationStarted(OperationKey, attempt, "Running")).ConfigureAwait(false);

                var context = new ManagedOperationContext(
                    attemptCancellation.Token,
                    _timeProvider,
                    new AttemptProgress(this, attempt),
                    attempt,
                    () => Volatile.Read(ref _expired) != 0);

                // 先启动期限监听，覆盖业务委托的完整执行（含返回 Task 前的同步阻塞或长计算）
                deadlineTask = WatchDeadlineAsync(startedAt, attemptCancellation, deadlineStop.Token);

                // 业务委托在独立线程池任务中启动并解包，捕获执行上下文与同步/异步异常，避免同步阻塞拖垮期限调度
                handlerTask = Task.Run(() => handler(context));

                var completed = await Task.WhenAny(handlerTask, deadlineTask).ConfigureAwait(false);

                if (completed == deadlineTask && await deadlineTask.ConfigureAwait(false))
                {
                    // 期限守卫判定硬超时
                    if (!await StopAttemptAsync(handlerTask, attemptCancellation, attempt).ConfigureAwait(false))
                    {
                        terminalSent = true;
                        throw new OperationUnresponsiveException(OperationKey, attempt, _cleanupGrace);
                    }

                    await SendAttemptEndedEnvelopeAsync(ProtocolMessages.CreateAttemptEnded(OperationKey, attempt, "Expired", "Deadline")).ConfigureAwait(false);
                    terminalSent = true;
                    throw new OperationDeadlineExceededException(OperationKey, attempt);
                }

                var result = await handlerTask.ConfigureAwait(false);
                if (!TryCompleteAttempt())
                {
                    if (Volatile.Read(ref _expired) != 0)
                    {
                        _ = TriggerAttemptCancellation(attemptCancellation);
                        await SendAttemptEndedEnvelopeAsync(ProtocolMessages.CreateAttemptEnded(OperationKey, attempt, "Expired", "Deadline")).ConfigureAwait(false);
                        terminalSent = true;
                        throw new OperationDeadlineExceededException(OperationKey, attempt);
                    }

                    throw new OperationCanceledException(attemptCancellation.Token);
                }

                await SendAttemptEndedEnvelopeAsync(ProtocolMessages.CreateAttemptEnded(OperationKey, attempt, "Completed")).ConfigureAwait(false);
                terminalSent = true;
                return result;
            }
            catch (OperationCanceledException) when (_rootCancellation.IsCancellationRequested
                || cancellationToken.IsCancellationRequested
                || _sessionCancellation.IsCancellationRequested
                || Volatile.Read(ref _ended) != 0)
            {
                if (!await StopAttemptAsync(handlerTask, attemptCancellation, attempt).ConfigureAwait(false))
                {
                    terminalSent = true;
                    throw new OperationUnresponsiveException(OperationKey, attempt, _cleanupGrace);
                }

                if (Volatile.Read(ref _expired) != 0 && !terminalSent)
                {
                    TryCompleteAttempt();
                    await SendAttemptEndedEnvelopeAsync(ProtocolMessages.CreateAttemptEnded(OperationKey, attempt, "Expired", "Deadline")).ConfigureAwait(false);
                    terminalSent = true;
                    throw new OperationDeadlineExceededException(OperationKey, attempt);
                }

                TryCompleteAttempt();
                if (!terminalSent)
                {
                    await SendAttemptEndedEnvelopeAsync(ProtocolMessages.CreateAttemptEnded(OperationKey, attempt, "Cancelled", "ParentCancellation")).ConfigureAwait(false);
                    terminalSent = true;
                }

                throw;
            }
            catch (Exception) when (!terminalSent)
            {
                if (!await StopAttemptAsync(handlerTask, attemptCancellation, attempt).ConfigureAwait(false))
                {
                    terminalSent = true;
                    throw new OperationUnresponsiveException(OperationKey, attempt, _cleanupGrace);
                }

                if (!TryCompleteAttempt() && Volatile.Read(ref _expired) != 0)
                {
                    await SendAttemptEndedEnvelopeAsync(ProtocolMessages.CreateAttemptEnded(OperationKey, attempt, "Expired", "Deadline")).ConfigureAwait(false);
                    terminalSent = true;
                    throw new OperationDeadlineExceededException(OperationKey, attempt);
                }

                if (_rootCancellation.IsCancellationRequested || cancellationToken.IsCancellationRequested
                    || _sessionCancellation.IsCancellationRequested || Volatile.Read(ref _ended) != 0)
                {
                    await SendAttemptEndedEnvelopeAsync(ProtocolMessages.CreateAttemptEnded(OperationKey, attempt, "Cancelled", "ParentCancellation")).ConfigureAwait(false);
                    terminalSent = true;
                    throw new OperationCanceledException(attemptCancellation.Token);
                }

                await SendAttemptEndedEnvelopeAsync(ProtocolMessages.CreateAttemptEnded(OperationKey, attempt, "Failed")).ConfigureAwait(false);
                terminalSent = true;
                throw;
            }
            finally
            {
                try { deadlineStop.Cancel(); } catch { }
                _progress.DiscardPending();
                if (deadlineTask is not null)
                {
                    try { await deadlineTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (deadlineStop.IsCancellationRequested) { }
                    catch { }
                }

                if (!terminalSent) { TryCompleteAttempt(); }

                Task? cancelTask;
                lock (_policyGate)
                {
                    _attemptClosed = true;
                    _activeCancellation = null;
                    cancelTask = _activeAttemptCancelTask;
                }

                var attemptUsers = new List<Task>();
                if (cancelTask is not null) attemptUsers.Add(cancelTask);
                if (handlerTask is not null) attemptUsers.Add(handlerTask);

                if (attemptUsers.Count > 0)
                {
                    _ = Task.WhenAll(attemptUsers).ContinueWith(static (t, state) =>
                    {
                        _ = t.Exception;
                        var (attCts, dlCts, exitTcs) = ((CancellationTokenSource, CancellationTokenSource, TaskCompletionSource))state!;
                        try { attCts.Dispose(); } catch { }
                        try { dlCts.Dispose(); } catch { }
                        exitTcs.TrySetResult();
                    }, (attemptCancellation, deadlineStop, attemptExitTcs), TaskScheduler.Default);
                }
                else
                {
                    try { attemptCancellation.Dispose(); } catch { }
                    try { deadlineStop.Dispose(); } catch { }
                    attemptExitTcs.TrySetResult();
                }
            }
        }
        finally
        {
            TaskCompletionSource? completion;
            lock (_policyGate)
            {
                _attemptClosed = true;
                _activeCancellation = null;
                completion = _activeCompletion;
                _activeCompletion = null;
            }

            _executionGate.Release();
            completion?.TrySetResult();

            if (Volatile.Read(ref _disposeRequested) != 0)
            {
                await CompleteDisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 异步注销并释放受管操作资源。
    /// 异步隔离派发正在执行尝试的取消并在宽限期内等待其收尾，随后发送 OperationEnd 通知注销操作。
    /// 本地注销置于 finally 块中，杜绝外部取消回调抛错或阻塞中断注销流程。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _ended, 1);

        Task? activeTask;
        CancellationTokenSource? activeCancellation;
        lock (_policyGate)
        {
            Volatile.Write(ref _ended, 1);
            activeTask = _activeCompletion?.Task;
            activeCancellation = _activeCancellation;
        }

        // 异步隔离派发取消通知，统一由尝试取消执行持有器管理，杜绝外部注册回调同步阻塞或抛错
        if (activeCancellation is not null)
        {
            _ = TriggerAttemptCancellation(activeCancellation);
        }

        try
        {
            if (activeTask is not null)
            {
                try
                {
                    await activeTask.WaitAsync(_cleanupGrace).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    try
                    {
                        await SendEnvelopeAsync(ProtocolMessages.CreateOperationState(
                            "OperationUnresponsive", OperationKey, Attempts, "Unresponsive", $"释放操作后 {_cleanupGrace} 内未停止")).ConfigureAwait(false);
                    }
                    catch { /* 写出无响应信封异常安全吸收，不影响本地释放推进 */ }
                }
            }
        }
        finally
        {
            await CompleteDisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 向当前受管操作汇报阶段进度。
    /// 遵循操作与尝试生命周期状态，在尝试运行期间投递至有界限频通道异步发送；终态或未开始时安全忽略。
    /// </summary>
    /// <param name="progress">进度模型实例。</param>
    public void ReportProgress(RunProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        lock (_policyGate)
        {
            if (_ended == 0 && _attemptState == 1)
            {
                _progress.Report(progress);
            }
        }
    }

    /// <summary>
    /// 尝试将当前活跃尝试标记为终态完成。
    /// </summary>
    private bool TryCompleteAttempt()
    {
        lock (_policyGate)
        {
            if (_attemptState != 1)
            {
                return false;
            }

            _attemptState = 2;
            _progress.DiscardPending();
            return true;
        }
    }

    /// <summary>
    /// 触发当前活跃尝试的异步隔离取消并返回取消任务。
    /// 统一期限守卫、尝试停止、释放及会话收尾各路径的取消执行所有权，
    /// 杜绝外部业务注册的取消回调同步阻塞消息分发、到期通知或无响应判定。
    /// </summary>
    /// <param name="attemptCancellation">尝试局部取消源。</param>
    /// <returns>代表实际取消执行的后台任务。</returns>
    private Task TriggerAttemptCancellation(CancellationTokenSource attemptCancellation)
    {
        lock (_policyGate)
        {
            if (_attemptClosed || _activeCancellation != attemptCancellation)
            {
                return _activeAttemptCancelTask ?? Task.CompletedTask;
            }

            if (_activeAttemptCancelTask is not null)
            {
                return _activeAttemptCancelTask;
            }

            _activeAttemptCancelTask = Task.Run(() =>
            {
                try
                {
                    attemptCancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                catch
                {
                    // 吸收外部注册回调可能抛出的任何未捕获异常
                }
            });

            _ = _activeAttemptCancelTask.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
            return _activeAttemptCancelTask;
        }
    }

    /// <summary>
    /// 触发尝试取消并在宽限期内等待其停止。
    /// </summary>
    private async Task<bool> StopAttemptAsync(
        Task? handlerTask,
        CancellationTokenSource attemptCancellation,
        int attempt)
    {
        _ = TriggerAttemptCancellation(attemptCancellation);
        if (handlerTask is null)
        {
            return true;
        }

        try
        {
            await handlerTask.WaitAsync(_cleanupGrace).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) when (!handlerTask.IsCompleted)
        {
            Interlocked.Exchange(ref _ended, 1);
            TryCompleteAttempt();
            await SendEnvelopeAsync(ProtocolMessages.CreateOperationState(
                "OperationUnresponsive",
                OperationKey,
                attempt,
                "Unresponsive",
                $"取消后 {_cleanupGrace} 内未停止")).ConfigureAwait(false);
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 完成资源注销与通知写出流程。
    /// </summary>
    private async Task CompleteDisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeCompleted, 1) != 0)
        {
            return;
        }

        try
        {
            await _progress.DisposeAsync().ConfigureAwait(false);
            await SendEnvelopeAsync(ProtocolMessages.CreateOperationEnd(OperationKey, Attempts, "Ended")).ConfigureAwait(false);
        }
        catch
        {
            // 忽略写出或注销异常，确保本地操作注销必定完成
        }
        finally
        {
            _removeOperationAction(OperationKey);
        }
    }

    /// <summary>
    /// 过滤并上报指定尝试发出的进度更新。
    /// </summary>
    private void ReportAttempt(int attempt, RunProgress value)
    {
        lock (_policyGate)
        {
            if (_ended == 0 && _attemptState == 1 && _activeAttempt == attempt)
            {
                _progress.Report(value);
            }
        }
    }

    /// <summary>
    /// 尝试专用的进度观察者转发包装器。
    /// </summary>
    private sealed class AttemptProgress(ManagedOperation owner, int attempt) : IProgress<RunProgress>
    {
        /// <summary>
        /// 汇报当前尝试的进度更新。
        /// </summary>
        public void Report(RunProgress value) => owner.ReportAttempt(attempt, value);
    }

    /// <summary>
    /// 创建新的异步通知完成源信号。
    /// </summary>
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
