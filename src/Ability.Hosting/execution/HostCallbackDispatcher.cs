// <copyright file="HostCallbackDispatcher.cs" company="MProject">
// Copyright (c) MProject. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using System.Threading.Channels;
using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// 宿主回调隔离派发器。
/// 负责快照、操作进度及调用进度回调的有界缓冲、超时隔离与退出排空，防止慢回调阻塞主接收调度循环。
/// 实现 IAsyncDisposable 保证全生命周期托管与异常退出时的可靠收尾。
/// </summary>
internal sealed class HostCallbackDispatcher : IAsyncDisposable
{
    /// <summary>默认单次回调执行超时时限。</summary>
    private static readonly TimeSpan DefaultCallbackTimeout = TimeSpan.FromSeconds(2);

    /// <summary>内部捕获的回调异常与超时问题队列。</summary>
    private readonly ConcurrentQueue<HostCleanupIssue> _capturedIssues = new();

    /// <summary>快照与操作进度隔离分发通道。</summary>
    private readonly Channel<Action>? _isolatedDispatchChannel;

    /// <summary>快照与操作进度隔离分发后台任务。</summary>
    private readonly Task? _isolatedDispatchTask;

    /// <summary>快照与操作进度隔离分发取消令牌源。</summary>
    private readonly CancellationTokenSource? _isolatedDispatchCts;

    /// <summary>任务整体进度分发通道。</summary>
    private readonly Channel<HostProgress>? _progressChannel;

    /// <summary>任务整体进度分发后台任务。</summary>
    private readonly Task? _progressWorkerTask;

    /// <summary>任务整体进度分发取消令牌源。</summary>
    private readonly CancellationTokenSource? _progressWorkerCts;

    /// <summary>诊断消息隔离分发通道。</summary>
    private readonly Channel<string>? _diagnosticChannel;

    /// <summary>诊断消息分发后台任务。</summary>
    private readonly Task? _diagnosticWorkerTask;

    /// <summary>诊断消息分发取消令牌源。</summary>
    private readonly CancellationTokenSource? _diagnosticWorkerCts;

    /// <summary>诊断取消令牌源释放延续任务。</summary>
    private Task? _diagnosticCtsDisposalTask;

    /// <summary>诊断生产者任务集合锁。</summary>
    private readonly Lock _producerTasksLock = new();

    /// <summary>所有可能产生诊断消息的活跃受管生产者任务集合。</summary>
    private readonly HashSet<Task> _producerTasks = [];

    /// <summary>后台收尾展开持有任务。</summary>
    private Task? _finalDrainTask;

    /// <summary>排空收尾任务引用锁。</summary>
    private readonly Lock _drainLock = new();

    /// <summary>已缓存的排空收尾异步任务。</summary>
    private Task? _drainTask;

    /// <summary>宿主运行选项配置。</summary>
    private readonly HostRuntimeOptions _options;

    /// <summary>
    /// 记录派发器内部捕获的收尾问题或回调异常。
    /// </summary>
    /// <param name="stage">发生阶段名称。</param>
    /// <param name="errorName">错误类型名称。</param>
    /// <param name="message">错误详细描述。</param>
    public void RecordIssue(string stage, string errorName, string message)
    {
        _capturedIssues.Enqueue(new HostCleanupIssue(stage, errorName, message));
    }

    /// <summary>
    /// 获取派发器生命周期内捕获的所有问题列表快照。
    /// </summary>
    /// <returns>收尾问题列表快照。</returns>
    public IReadOnlyList<HostCleanupIssue> GetCapturedIssues() => _capturedIssues.ToArray();

    /// <summary>释放阶段是否发生过异常，由宿主收尾按事实读取。</summary>
    private volatile bool _disposalFailed;

    /// <summary>获取释放阶段是否发生过异常。</summary>
    public bool HasDisposalFailure => _disposalFailed;

    /// <summary>
    /// 记录释放阶段异常，并置位释放失败事实；事实与问题分别供宿主判定与报告使用。
    /// </summary>
    /// <param name="ex">释放阶段异常。</param>
    private void RecordDisposalIssue(Exception ex)
    {
        _disposalFailed = true;
        RecordIssue("DispatcherDisposal", ex.GetType().Name, DescribeSafeError(ex));
    }

    /// <summary>
    /// 尝试释放单次回调延续自有的一个取消令牌源。
    /// 释放异常记录为脱敏问题并置位释放失败事实；两个取消源分别调用本步骤，前一个失败不跳过后一个。
    /// </summary>
    /// <param name="cts">本次回调延续自有的取消令牌源。</param>
    private void DisposeCallbackCancellationSource(CancellationTokenSource cts)
    {
        try
        {
            cts.Dispose();
        }
        catch (Exception ex)
        {
            RecordDisposalIssue(ex);
        }
    }

    /// <summary>
    /// 获取派发器是否已完全排空所有工作循环、在途回调及诊断工作者。
    /// </summary>
    public bool IsDrained
    {
        get
        {
            if (_isolatedDispatchTask is { IsCompleted: false }) return false;
            if (_progressWorkerTask is { IsCompleted: false }) return false;
            if (_diagnosticWorkerTask is { IsCompleted: false }) return false;
            if (_diagnosticCtsDisposalTask is { IsCompleted: false }) return false;
            if (_finalDrainTask is { IsCompleted: false }) return false;
            lock (_producerTasksLock)
            {
                if (_producerTasks.Count > 0) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 登记可能产生诊断的受管生产者任务，在任务完成后自动从集合中移除。
    /// </summary>
    /// <param name="task">生产者任务。</param>
    private void TrackProducerTask(Task task)
    {
        lock (_producerTasksLock)
        {
            _producerTasks.Add(task);
        }

        _ = task.ContinueWith(t =>
        {
            _ = t.Exception;
            lock (_producerTasksLock)
            {
                _producerTasks.Remove(task);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// 初始化宿主回调隔离派发器实例。
    /// </summary>
    /// <param name="options">宿主运行时选项。</param>
    public HostCallbackDispatcher(HostRuntimeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (options.DiagnosticCallback is not null)
        {
            var diagnosticCts = new CancellationTokenSource();
            _diagnosticWorkerCts = diagnosticCts;
            var diagnosticChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
            _diagnosticChannel = diagnosticChannel;
            _diagnosticWorkerTask = Task.Run(() => RunDiagnosticWorkerAsync(options, diagnosticChannel, diagnosticCts.Token));
        }

        if (options.OperationSnapshotCallback is not null || options.OperationProgressCallback is not null)
        {
            var isolatedCts = new CancellationTokenSource();
            _isolatedDispatchCts = isolatedCts;
            var isolatedChannel = Channel.CreateBounded<Action>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
            _isolatedDispatchChannel = isolatedChannel;
            _isolatedDispatchTask = Task.Run(() => RunIsolatedDispatchWorkerAsync(options, isolatedChannel, isolatedCts.Token), isolatedCts.Token);
        }

        if (options.ProgressCallback is not null)
        {
            var progressCts = new CancellationTokenSource();
            _progressWorkerCts = progressCts;
            var progressChannel = Channel.CreateBounded<HostProgress>(new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });
            _progressChannel = progressChannel;
            _progressWorkerTask = Task.Run(() => RunProgressWorkerAsync(options, progressChannel, progressCts.Token));
        }
    }

    /// <summary>
    /// 单次隔离派发回调的执行结果，用于决定后续快照是否继续派发。
    /// </summary>
    private enum IsolatedDispatchOutcome
    {
        /// <summary>回调已执行完成或已记录异常，可继续派发。</summary>
        Completed,

        /// <summary>回调执行超时，停止派发后续回调以防后台堆积。</summary>
        TimedOut,

        /// <summary>派发通道已取消，工作循环结束。</summary>
        Cancelled
    }

    /// <summary>
    /// 诊断消息隔离分发工作者：有界通道满时丢弃最旧项，取消后继续排空已入队消息。
    /// </summary>
    /// <param name="options">宿主运行时选项。</param>
    /// <param name="channel">诊断消息通道。</param>
    /// <param name="diagnosticToken">诊断工作者取消令牌。</param>
    /// <returns>工作者循环的异步完成任务。</returns>
    private async Task RunDiagnosticWorkerAsync(HostRuntimeOptions options, Channel<string> channel, CancellationToken diagnosticToken)
    {
        try
        {
            while (await channel.Reader.WaitToReadAsync(diagnosticToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var message))
                {
                    InvokeDiagnosticCallback(options, message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        while (channel.Reader.TryRead(out var remaining))
        {
            InvokeDiagnosticCallback(options, remaining);
        }
    }

    /// <summary>
    /// 执行一次诊断回调并隔离其异常，杜绝外部回调抛错影响收尾。
    /// </summary>
    /// <param name="options">宿主运行时选项。</param>
    /// <param name="message">诊断消息内容。</param>
    private void InvokeDiagnosticCallback(HostRuntimeOptions options, string message)
    {
        try
        {
            options.DiagnosticCallback!(message);
        }
        catch (Exception ex)
        {
            RecordIssue("DiagnosticCallback", ex.GetType().Name, $"诊断消息回调执行抛出异常: {DescribeSafeError(ex)}");
        }
    }

    /// <summary>
    /// 快照与操作进度隔离分发工作者：逐项串行派发，单次回调超时后停止后续派发。
    /// </summary>
    /// <param name="options">宿主运行时选项。</param>
    /// <param name="channel">隔离派发通道。</param>
    /// <param name="dispatchToken">派发通道取消令牌。</param>
    /// <returns>工作者循环的异步完成任务。</returns>
    private async Task RunIsolatedDispatchWorkerAsync(HostRuntimeOptions options, Channel<Action> channel, CancellationToken dispatchToken)
    {
        var callbackTimedOut = false;
        try
        {
            while (await channel.Reader.WaitToReadAsync(dispatchToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var action))
                {
                    if (callbackTimedOut)
                    {
                        continue;
                    }

                    var outcome = await InvokeIsolatedActionAsync(action, options, dispatchToken).ConfigureAwait(false);
                    if (outcome == IsolatedDispatchOutcome.Cancelled)
                    {
                        return;
                    }

                    if (outcome == IsolatedDispatchOutcome.TimedOut)
                    {
                        callbackTimedOut = true;
                    }
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 派发单个快照或操作进度回调：施加单次期限，迟到异常由延续任务观察。
    /// </summary>
    /// <param name="action">回调动作。</param>
    /// <param name="options">宿主运行时选项。</param>
    /// <param name="dispatchToken">派发通道取消令牌。</param>
    /// <returns>本次派发的执行结果。</returns>
    private async Task<IsolatedDispatchOutcome> InvokeIsolatedActionAsync(Action action, HostRuntimeOptions options, CancellationToken dispatchToken)
    {
        var actionCts = new CancellationTokenSource(DefaultCallbackTimeout, options.TimeProvider);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(actionCts.Token, dispatchToken);
        try
        {
            var task = Task.Run(action, linked.Token);
            var continuation = task.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception is { } ex)
                {
                    var baseEx = ex.GetBaseException();
                    RecordIssue("CallbackDispatcher", baseEx.GetType().Name, $"迟到快照/操作进度回调抛出异常: {DescribeSafeError(baseEx)}");
                    DispatchDiagnostic($"迟到快照/操作进度回调抛出异常: {DescribeSafeError(baseEx)}");
                }

                DisposeCallbackCancellationSource(linked);
                DisposeCallbackCancellationSource(actionCts);
            }, TaskScheduler.Default);
            TrackProducerTask(continuation);

            await task.WaitAsync(DefaultCallbackTimeout, options.TimeProvider, dispatchToken).ConfigureAwait(false);
            return IsolatedDispatchOutcome.Completed;
        }
        catch (TimeoutException)
        {
            ReportIsolatedCallbackTimeout();
            return IsolatedDispatchOutcome.TimedOut;
        }
        catch (OperationCanceledException) when (dispatchToken.IsCancellationRequested)
        {
            return IsolatedDispatchOutcome.Cancelled;
        }
        catch (OperationCanceledException)
        {
            ReportIsolatedCallbackTimeout();
            return IsolatedDispatchOutcome.TimedOut;
        }
        catch (Exception ex)
        {
            RecordIssue("CallbackDispatcher", ex.GetType().Name, $"快照/操作进度回调抛出异常: {DescribeSafeError(ex)}");
            DispatchDiagnostic($"快照/操作进度回调抛出异常: {DescribeSafeError(ex)}");
            return IsolatedDispatchOutcome.Completed;
        }
    }

    /// <summary>
    /// 记录并报告快照或操作进度回调超时，作为停止后续派发的依据。
    /// </summary>
    private void ReportIsolatedCallbackTimeout()
    {
        RecordIssue("CallbackDispatcher", "TimeoutException", "快照/操作进度回调执行超时，停止派发后续快照以防后台堆积。");
        DispatchDiagnostic("快照/操作进度回调执行超时，停止派发后续快照以防后台堆积。");
    }

    /// <summary>
    /// 有界调用进度分发工作者：逐项串行派发，单次回调超时后停止后续派发。
    /// </summary>
    /// <param name="options">宿主运行时选项。</param>
    /// <param name="channel">进度通道。</param>
    /// <param name="workerToken">进度工作者取消令牌。</param>
    /// <returns>工作者循环的异步完成任务。</returns>
    private async Task RunProgressWorkerAsync(HostRuntimeOptions options, Channel<HostProgress> channel, CancellationToken workerToken)
    {
        var callbackTimedOut = false;
        try
        {
            while (await channel.Reader.WaitToReadAsync(workerToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var hostProgress))
                {
                    if (callbackTimedOut)
                    {
                        continue;
                    }

                    var outcome = await InvokeProgressCallbackAsync(hostProgress, options, workerToken).ConfigureAwait(false);
                    if (outcome == IsolatedDispatchOutcome.Cancelled)
                    {
                        return;
                    }

                    if (outcome == IsolatedDispatchOutcome.TimedOut)
                    {
                        callbackTimedOut = true;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// 派发单条进度回调：施加单次期限，迟到异常由延续任务观察。
    /// </summary>
    /// <param name="hostProgress">进度通知内容。</param>
    /// <param name="options">宿主运行时选项。</param>
    /// <param name="workerToken">进度工作者取消令牌。</param>
    /// <returns>本次派发的执行结果。</returns>
    private async Task<IsolatedDispatchOutcome> InvokeProgressCallbackAsync(HostProgress hostProgress, HostRuntimeOptions options, CancellationToken workerToken)
    {
        var progressCts = new CancellationTokenSource(DefaultCallbackTimeout, options.TimeProvider);
        var linkedProgress = CancellationTokenSource.CreateLinkedTokenSource(progressCts.Token, workerToken);
        var progressToken = linkedProgress.Token;

        try
        {
            var task = Task.Run(() => options.ProgressCallback!(hostProgress), progressToken);
            var continuation = task.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception is { } ex)
                {
                    var baseEx = ex.GetBaseException();
                    RecordIssue("ProgressCallback", baseEx.GetType().Name, $"迟到进度回调抛出异常: {DescribeSafeError(baseEx)}");
                    DispatchDiagnostic($"迟到进度回调抛出异常: {DescribeSafeError(baseEx)}");
                }

                DisposeCallbackCancellationSource(linkedProgress);
                DisposeCallbackCancellationSource(progressCts);
            }, TaskScheduler.Default);
            TrackProducerTask(continuation);

            await task.WaitAsync(DefaultCallbackTimeout, options.TimeProvider, workerToken).ConfigureAwait(false);
            return IsolatedDispatchOutcome.Completed;
        }
        catch (TimeoutException)
        {
            ReportProgressCallbackTimeout();
            return IsolatedDispatchOutcome.TimedOut;
        }
        catch (OperationCanceledException) when (workerToken.IsCancellationRequested)
        {
            return IsolatedDispatchOutcome.Cancelled;
        }
        catch (OperationCanceledException)
        {
            ReportProgressCallbackTimeout();
            return IsolatedDispatchOutcome.TimedOut;
        }
        catch (Exception ex)
        {
            RecordIssue("ProgressCallback", ex.GetType().Name, $"进度回调抛出异常: {DescribeSafeError(ex)}");
            DispatchDiagnostic($"进度回调抛出异常: {DescribeSafeError(ex)}");
            return IsolatedDispatchOutcome.Completed;
        }
    }

    /// <summary>
    /// 记录并报告进度回调超时，作为停止后续派发的依据。
    /// </summary>
    private void ReportProgressCallbackTimeout()
    {
        RecordIssue("ProgressCallback", "TimeoutException", "进度回调执行超时，停止派发后续进度回调以防未完成后台任务累积。");
        DispatchDiagnostic("进度回调执行超时，停止派发后续进度回调以防未完成后台任务累积。");
    }

    /// <summary>
    /// 派发受管操作运行快照。
    /// </summary>
    /// <param name="snapshot">操作快照对象。</param>
    public void DispatchSnapshot(OperationSnapshot snapshot)
    {
        if (_options.OperationSnapshotCallback is { } cb)
        {
            _isolatedDispatchChannel?.Writer.TryWrite(() => cb(snapshot));
        }
    }

    /// <summary>
    /// 派发受管操作进度数据。
    /// </summary>
    /// <param name="prog">操作进度信封数据体。</param>
    public void DispatchOperationProgress(ProgressBody prog)
    {
        if (_options.OperationProgressCallback is { } cb)
        {
            _isolatedDispatchChannel?.Writer.TryWrite(() => cb(prog));
        }
    }

    /// <summary>
    /// 派发宿主任务整体进度。
    /// </summary>
    /// <param name="hostProgress">宿主任务进度对象。</param>
    public void DispatchHostProgress(HostProgress hostProgress)
    {
        _progressChannel?.Writer.TryWrite(hostProgress);
    }

    /// <summary>
    /// 异步隔离派发诊断日志通知，不阻塞调用者线程。
    /// </summary>
    /// <param name="message">诊断消息内容。</param>
    public void DispatchDiagnostic(string message)
    {
        _diagnosticChannel?.Writer.TryWrite(message);
    }

    /// <summary>
    /// 完成通道并等待后台工作任务排空退出。
    /// 具有幂等性：后续重复调用直接返回已有排空任务，确保正常路径与异常路径共享唯一收尾责任，避免重复排空与重复释放。
    /// </summary>
    /// <returns>异步等待任务。</returns>
    public Task CompleteAndDrainAsync()
    {
        lock (_drainLock)
        {
            _drainTask ??= DrainInternalAsync();
            return _drainTask;
        }
    }

    /// <summary>
    /// 异步释放派发器持有的资源与通道，执行排空收尾。
    /// 抑制收尾过程中的次级异常，确保不覆盖调用方原始准备或业务异常。
    /// </summary>
    /// <returns>异步释放任务。</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await CompleteAndDrainAsync().ConfigureAwait(false);
        }
        catch
        {
            // 抑制排空异常，保留原始异常
        }
    }

    /// <summary>
    /// 内部执行通道完成与后台工作任务排空收尾。
    /// </summary>
    /// <returns>异步等待任务。</returns>
    private async Task DrainInternalAsync()
    {
        CloseIsolatedWriters();
        var allLoopsTask = CollectDispatchLoopTask();

        // 前台成功完成全部生产者的排空后，关闭诊断通道并有限等待诊断工作者
        if (await WaitDispatchLoopsAsync(allLoopsTask).ConfigureAwait(false)
            && await WaitProducerContinuationsAsync().ConfigureAwait(false))
        {
            await CloseDiagnosticWorkerSafelyAsync().ConfigureAwait(false);
            return;
        }

        // 前台未能全部完成时，由后台展开持有任务接管最终收尾
        StartFinalDrainTask(allLoopsTask);
    }

    /// <summary>
    /// 关闭快照隔离派发通道与整体进度通道写入端，确认不会再登记新的回调生产者。
    /// </summary>
    private void CloseIsolatedWriters()
    {
        _isolatedDispatchChannel?.Writer.TryComplete();
        _progressChannel?.Writer.TryComplete();
    }

    /// <summary>
    /// 汇总快照派发与进度派发的工作循环任务；未启动的工作者不参与等待。
    /// </summary>
    /// <returns>全部工作循环的合并任务。</returns>
    private Task CollectDispatchLoopTask()
    {
        var dispatchLoops = new List<Task>(2);
        if (_isolatedDispatchTask is not null)
        {
            dispatchLoops.Add(_isolatedDispatchTask);
        }

        if (_progressWorkerTask is not null)
        {
            dispatchLoops.Add(_progressWorkerTask);
        }

        return dispatchLoops.Count > 0 ? Task.WhenAll(dispatchLoops) : Task.CompletedTask;
    }

    /// <summary>
    /// 前台有限等待派发工作循环物理退出。
    /// </summary>
    /// <param name="allLoopsTask">工作循环合并任务。</param>
    /// <returns>在预算内全部退出时为 true。</returns>
    private async Task<bool> WaitDispatchLoopsAsync(Task allLoopsTask)
    {
        try
        {
            await allLoopsTask.WaitAsync(TimeSpan.FromSeconds(1), _options.TimeProvider).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            RecordIssue("CallbackDispatcher", "TimeoutException", "派发工作循环在退出时未能在 1 秒内完全结束。");
            DispatchDiagnostic("派发工作循环在退出时未能在 1 秒内完全结束。");
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 在派发循环已退出的前提下，有限等待在途回调及其延续任务结束。
    /// </summary>
    /// <returns>在预算内全部结束或本无剩余任务时为 true。</returns>
    private async Task<bool> WaitProducerContinuationsAsync()
    {
        // 派发循环已完全物理退出，确认绝不会再产生或登记新的回调任务；集合中仅剩在途回调及其延续任务
        Task[] remainingContinuations;
        lock (_producerTasksLock)
        {
            remainingContinuations = [.. _producerTasks];
        }

        if (remainingContinuations.Length == 0)
        {
            return true;
        }

        try
        {
            await Task.WhenAll(remainingContinuations).WaitAsync(TimeSpan.FromSeconds(1), _options.TimeProvider).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            RecordIssue("CallbackDispatcher", "TimeoutException", "回调延续任务在退出时未能在 1 秒内完全结束。");
            DispatchDiagnostic("回调延续任务在退出时未能在 1 秒内完全结束。");
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 启动后台最终排空：先等派发循环物理退出，再等剩余生产者结束，最后关闭诊断工作者。
    /// </summary>
    /// <param name="allLoopsTask">工作循环合并任务。</param>
    private void StartFinalDrainTask(Task allLoopsTask)
    {
        _finalDrainTask = Task.Run(async () =>
        {
            try
            {
                // 先等待派发循环彻底物理退出，确认绝不会再增加新生产者
                try
                {
                    await allLoopsTask.ConfigureAwait(false);
                }
                catch
                {
                }

                // 派发循环已物理退出，此时集合内只剩有限的回调及其延续任务，且绝不会再增加
                Task[] finalRemaining;
                lock (_producerTasksLock)
                {
                    finalRemaining = [.. _producerTasks];
                }

                if (finalRemaining.Length > 0)
                {
                    try
                    {
                        await Task.WhenAll(finalRemaining).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                // 全部生产者物理退出后，最终安全关闭诊断通道并排空诊断工作者
                await CloseDiagnosticWorkerSafelyAsync().ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// 安全关闭诊断工作者，排空剩余诊断消息并释放取消令牌源。
    /// </summary>
    /// <returns>异步关闭与排空任务。</returns>
    private async Task CloseDiagnosticWorkerSafelyAsync()
    {
        try
        {
            if (_diagnosticChannel is not null)
            {
                _diagnosticChannel.Writer.TryComplete();
                try
                {
                    _diagnosticWorkerCts?.Cancel();
                }
                catch (Exception ex)
                {
                    RecordIssue("DiagnosticWorker", ex.GetType().Name, DescribeSafeError(ex));
                }

                if (_diagnosticWorkerTask is not null)
                {
                    var workerExitedInTime = false;
                    try
                    {
                        await _diagnosticWorkerTask.WaitAsync(TimeSpan.FromSeconds(1), _options.TimeProvider).ConfigureAwait(false);
                        workerExitedInTime = true;
                    }
                    catch (TimeoutException)
                    {
                        RecordIssue("DiagnosticWorkerTimeout", "TimeoutException", "诊断工作者在 1 秒超时预算内未完全退出，实际排空在后台继续执行。");
                    }
                    catch (Exception ex)
                    {
                        RecordIssue("DiagnosticWorker", ex.GetType().Name, DescribeSafeError(ex));
                    }

                    _diagnosticCtsDisposalTask ??= _diagnosticWorkerTask.ContinueWith(t =>
                    {
                        _ = t.Exception;
                        try
                        {
                            _diagnosticWorkerCts?.Dispose();
                        }
                        catch (Exception ex)
                        {
                            RecordDisposalIssue(ex);
                        }
                    }, TaskScheduler.Default);

                    if (workerExitedInTime)
                    {
                        try
                        {
                            await _diagnosticCtsDisposalTask.WaitAsync(TimeSpan.FromSeconds(1), _options.TimeProvider).ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            RecordIssue("DispatcherDrainTimeout", "TimeoutException", "诊断取消令牌源释放前台等待超时（>1s），实际释放在后台继续执行。");
                        }
                        catch (Exception ex)
                        {
                            RecordDisposalIssue(ex);
                        }
                    }
                }
                else
                {
                    try
                    {
                        _diagnosticWorkerCts?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        RecordDisposalIssue(ex);
                    }
                }
            }

            try
            {
                _isolatedDispatchCts?.Dispose();
            }
            catch (Exception ex)
            {
                RecordDisposalIssue(ex);
            }

            try
            {
                _progressWorkerCts?.Dispose();
            }
            catch (Exception ex)
            {
                RecordDisposalIssue(ex);
            }
        }
        catch (Exception ex)
        {
            RecordIssue("DispatcherDisposal", ex.GetType().Name, DescribeSafeError(ex));
        }
    }

    /// <summary>
    /// 获取对外部观察安全的异常简要描述。
    /// </summary>
    /// <param name="ex">异常对象。</param>
    /// <returns>安全的异常描述文本或异常类型名。</returns>
    private static string DescribeSafeError(Exception ex) =>
        ex is ISafeFaultDescription safe && !string.IsNullOrWhiteSpace(safe.SafeDescription)
            ? safe.SafeDescription
            : ex.GetType().Name;

    /// <summary>
    /// 等待所有派发循环、回调延续任务及诊断工作者完全物理退出。
    /// 供 <see cref="HostRunLifetime.Completion"/> 使用，不设超时限制，保证提供方安全释放依赖。
    /// </summary>
    /// <returns>等待完全退出的异步任务。</returns>
    public async Task WaitAllDrainedAsync()
    {
        await CompleteAndDrainAsync().ConfigureAwait(false);
        if (_finalDrainTask is { } finalDrain)
        {
            try
            {
                await finalDrain.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        // 关键：不设有限超时，直接等待诊断工作者真正物理完全退出
        if (_diagnosticWorkerTask is { } diagTask)
        {
            try
            {
                await diagTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (_diagnosticCtsDisposalTask is { } ctsTask)
        {
            try
            {
                await ctsTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }
}
