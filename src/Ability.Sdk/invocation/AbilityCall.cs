using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Ability.Core;
using Ability.Model;
using Ability.Role;

namespace Ability.Sdk;

/// <summary>
/// 子调用静态对外门面类，提供一元与流式子 Launcher 调用能力（支持业务模型与 DTO 等纯数据载荷）。
/// 负责前置输入载荷校验、管道与进程启动、背压传递、结果流转及异常安全回收。
/// </summary>
public static class AbilityCall
{
    /// <summary>
    /// 诊断记录使用的类别。
    /// </summary>
    private const string DiagnosticCategory = "Ability.Sdk.AbilityCall";

    /// <summary>
    /// 流式子调用协商的默认初始信用额度窗口。
    /// </summary>
    private const int DefaultInitialResultCredit = 4;

    /// <summary>
    /// 发起一元子调用：启动子 Launcher 进程、发送任务输入并等待最终一元业务结果。
    /// </summary>
    /// <typeparam name="TInput">输入模型类型。</typeparam>
    /// <typeparam name="TOutput">输出数据类型（可为业务模型或 DTO）。</typeparam>
    /// <param name="launcherPath">目标子 Launcher 入口路径。</param>
    /// <param name="input">输入业务模型。</param>
    /// <param name="inputJsonTypeInfo">输入模型的序列化元数据。</param>
    /// <param name="outputJsonTypeInfo">输出数据结果的反序列化元数据。</param>
    /// <param name="cancellationToken">局部取消令牌。</param>
    /// <param name="timeProvider">时间提供者。</param>
    /// <param name="progress">可选的本地进度观察者。</param>
    /// <returns>子调用的业务输出结果。</returns>
    public static async Task<TOutput> InvokeAsync<TInput, TOutput>(
        string launcherPath,
        TInput input,
        JsonTypeInfo<TInput> inputJsonTypeInfo,
        JsonTypeInfo<TOutput> outputJsonTypeInfo,
        CancellationToken cancellationToken,
        TimeProvider timeProvider,
        IProgress<RunProgress>? progress = null)
    {
        var operation = await AbilityRuntime.RegisterOperationAsync(
            "ChildLauncher",
            launcherPath,
            new RunPolicyMdl { MaxRetries = 0, ProgressInterval = TimeSpan.FromMilliseconds(250) },
            cancellationToken,
            timeProvider,
            progress);

        await using (operation)
        {
            return await operation.ExecuteAsync(
                context => InvokeUnaryCoreAsync(
                    launcherPath,
                    input,
                    inputJsonTypeInfo,
                    outputJsonTypeInfo,
                    context.CancellationToken,
                    context.TimeProvider,
                    context.Progress),
                cancellationToken);
        }
    }

    /// <summary>
    /// 一元子调用核心执行逻辑。
    /// </summary>
    private static async Task<TOutput> InvokeUnaryCoreAsync<TInput, TOutput>(
        string launcherPath,
        TInput input,
        JsonTypeInfo<TInput> inputJsonTypeInfo,
        JsonTypeInfo<TOutput> outputJsonTypeInfo,
        CancellationToken cancellationToken,
        TimeProvider timeProvider,
        IProgress<RunProgress>? progress)
    {
        var session = AbilityRuntime.RequireSession();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            session.BusinessCancellationToken);

        progress?.Report(new RunProgress { Stage = "ChildStart", Unit = "calls", Completed = 0, Total = 1 });

        var inputFrame = IpcFrame.EncodeTaskInputFrame(
            session.TaskId,
            session.TaskTempPath,
            typeof(TInput).Name,
            input,
            inputJsonTypeInfo,
            AbilityTracing.CurrentTraceParent,
            AbilityTracing.CurrentTraceState);

        await using var child = await StartAsync(
            session,
            launcherPath,
            inputFrame,
            linkedCancellation.Token,
            timeProvider);

        try
        {
            var response = await child.ReadDataAsync(linkedCancellation.Token);
            if (response.Type == ProtocolConstants.MessageTypes.SubmitFault)
            {
                var fault = ProtocolMessages.ReadBody<SubmitFaultBody>(response);
                throw new AbilityRequestFaultException(fault.Kind, fault.Message, fault.Site);
            }

            if (response.Type != ProtocolConstants.MessageTypes.SubmitResult)
            {
                throw new ProtocolException($"子 Launcher 一元调用收到意外消息类型：{response.Type}。");
            }

            var result = ProtocolMessages.ReadBody<SubmitResultBody>(response);
            var output = JsonSerializer.Deserialize(result.Payload, outputJsonTypeInfo)
                ?? throw new ProtocolException("子 Launcher 返回了空业务结果。");

            var reaped = await child.StopAndConfirmAsync("Completed");
            if (reaped.ExitCode != 0)
            {
                throw AbilityRequestFaultException.Reviewed(
                    "ChildExitCode",
                    $"子 Launcher 已提交结果，但进程退出码为 {reaped.ExitCode?.ToString() ?? "unknown"}。");
            }

            progress?.Report(new RunProgress { Stage = "ChildEnd", Unit = "calls", Completed = 1, Total = 1 });
            return output;
        }
        catch (Exception primaryException)
        {
            ChildReapedBody? reaped = null;
            try
            {
                reaped = await child.StopAndConfirmAsync(
                    linkedCancellation.IsCancellationRequested ? "Cancelled" : "Faulted");
            }
            catch (Exception cleanupException)
            {
                primaryException.Data["ChildCleanupFailure"] = cleanupException.ToString();
            }

            if (!linkedCancellation.IsCancellationRequested
                && primaryException is not OperationCanceledException
                && primaryException is not AbilityRequestFaultException
                && reaped?.ExitCode is int exitCode && exitCode != 0)
            {
                throw AbilityRequestFaultException.Reviewed(
                    "ChildProcessExit",
                    $"子调用 {child.InvocationKey} 已退出，宿主确认 exit code {exitCode}。");
            }

            throw;
        }
    }

    /// <summary>
    /// 发起流式子调用：启动子 Launcher 进程并以异步可枚举流形式接收子进程逐项产出的元素。
    /// </summary>
    /// <typeparam name="TInput">输入模型类型。</typeparam>
    /// <typeparam name="TOutput">流元素数据类型（可为业务模型或 DTO）。</typeparam>
    /// <param name="launcherPath">目标子 Launcher 入口路径。</param>
    /// <param name="input">输入业务模型。</param>
    /// <param name="inputJsonTypeInfo">输入模型的序列化元数据。</param>
    /// <param name="outputJsonTypeInfo">输出流元素数据的反序列化元数据。</param>
    /// <param name="cancellationToken">局部取消令牌。</param>
    /// <param name="timeProvider">时间提供者。</param>
    /// <param name="progress">可选的本地进度观察者。</param>
    /// <returns>子调用的异步可枚举结果流。</returns>
    public static IAsyncEnumerable<TOutput> InvokeStreamAsync<TInput, TOutput>(
        string launcherPath,
        TInput input,
        JsonTypeInfo<TInput> inputJsonTypeInfo,
        JsonTypeInfo<TOutput> outputJsonTypeInfo,
        CancellationToken cancellationToken,
        TimeProvider timeProvider,
        IProgress<RunProgress>? progress = null)
        => InvokeStreamCoreAsync(
            launcherPath,
            input,
            inputJsonTypeInfo,
            outputJsonTypeInfo,
            cancellationToken,
            timeProvider,
            progress);

    /// <summary>
    /// 流式子调用核心执行逻辑，将生产任务与消费枚举通过单容量通道解耦并传递背压。
    /// </summary>
    private static async IAsyncEnumerable<TOutput> InvokeStreamCoreAsync<TInput, TOutput>(
        string launcherPath,
        TInput input,
        JsonTypeInfo<TInput> inputJsonTypeInfo,
        JsonTypeInfo<TOutput> outputJsonTypeInfo,
        CancellationToken cancellationToken,
        TimeProvider timeProvider,
        IProgress<RunProgress>? progress,
        [EnumeratorCancellation] CancellationToken enumerationCancellation = default)
    {
        var session = AbilityRuntime.RequireSession();
        var operation = await AbilityRuntime.RegisterOperationAsync(
            "ChildLauncherStream",
            launcherPath,
            new RunPolicyMdl { MaxRetries = 0, ProgressInterval = TimeSpan.FromMilliseconds(250) },
            cancellationToken,
            timeProvider,
            progress).ConfigureAwait(false);

        await using var sequence = new ChildStreamSequence<TInput, TOutput>(
            session,
            operation,
            launcherPath,
            input,
            inputJsonTypeInfo,
            outputJsonTypeInfo,
            cancellationToken,
            enumerationCancellation);

        await foreach (var item in sequence.WithCancellation(enumerationCancellation).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    /// <summary>
    /// 流式子调用内部序列与收尾所有权管理器，协调生产者任务、通道消费与有限预算清理。
    /// </summary>
    private sealed class ChildStreamSequence<TInput, TOutput> : IAsyncEnumerable<TOutput>, IAsyncEnumerator<TOutput>
    {
        /// <summary>所属 Ability 会话实例。</summary>
        private readonly AbilitySession _session;
        /// <summary>受管子调用操作实例。</summary>
        private readonly ManagedOperation _operation;
        /// <summary>关联的取消令牌源。</summary>
        private readonly CancellationTokenSource _linkedCancellation;
        /// <summary>流输出缓冲通道。</summary>
        private readonly Channel<StreamOutput<TOutput>> _output;
        /// <summary>后台生产者任务。</summary>
        private readonly Task<bool> _producer;
        /// <summary>调用方原始取消令牌。</summary>
        private readonly CancellationToken _cancellationToken;
        /// <summary>枚举请求取消令牌。</summary>
        private readonly CancellationToken _enumerationCancellation;
        /// <summary>当前流式输出元素。</summary>
        private TOutput _current = default!;
        /// <summary>首个捕获的主异常或取消。</summary>
        private Exception? _primaryException;
        /// <summary>释放状态标记（0 为未释放，1 为已释放）。</summary>
        private int _disposed;

        /// <summary>
        /// 初始化子调用流序列实例。
        /// </summary>
        /// <param name="session">所属 Ability 会话。</param>
        /// <param name="operation">受管子调用操作。</param>
        /// <param name="launcherPath">启动器绝对路径。</param>
        /// <param name="input">强类型输入载荷。</param>
        /// <param name="inputJsonTypeInfo">输入 JSON 类型信息元数据。</param>
        /// <param name="outputJsonTypeInfo">输出流元素 JSON 类型信息元数据。</param>
        /// <param name="cancellationToken">调用方取消令牌。</param>
        /// <param name="enumerationCancellation">枚举取消令牌。</param>
        public ChildStreamSequence(
            AbilitySession session,
            ManagedOperation operation,
            string launcherPath,
            TInput input,
            JsonTypeInfo<TInput> inputJsonTypeInfo,
            JsonTypeInfo<TOutput> outputJsonTypeInfo,
            CancellationToken cancellationToken,
            CancellationToken enumerationCancellation)
        {
            _session = session;
            _operation = operation;
            _cancellationToken = cancellationToken;
            _enumerationCancellation = enumerationCancellation;

            _linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                enumerationCancellation,
                session.BusinessCancellationToken);

            _output = Channel.CreateBounded<StreamOutput<TOutput>>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });

            _producer = operation.ExecuteAsync(
                context => ProduceStreamAsync(session, launcherPath, input, inputJsonTypeInfo, outputJsonTypeInfo, context, _output.Writer),
                _linkedCancellation.Token);

            _ = CompleteStreamOutputAsync(_producer, _output.Writer);
        }

        /// <summary>
        /// 获取异步枚举器实例。
        /// </summary>
        /// <param name="cancellationToken">可选的取消令牌。</param>
        /// <returns>异步枚举器自身。</returns>
        public IAsyncEnumerator<TOutput> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        /// <summary>
        /// 获取当前枚举的流输出项。
        /// </summary>
        public TOutput Current => _current;

        /// <summary>
        /// 异步推进并读取下一个流输出项。
        /// </summary>
        /// <returns>若成功读取到下一个元素返回 true；若流正常结束返回 false。</returns>
        public async ValueTask<bool> MoveNextAsync()
        {
            try
            {
                while (await _output.Reader.WaitToReadAsync(_linkedCancellation.Token).ConfigureAwait(false))
                {
                    if (_output.Reader.TryRead(out var item))
                    {
                        _current = item.Value;
                        item.Consumed.TrySetResult();
                        return true;
                    }
                }

                await _producer.ConfigureAwait(false);
                return false;
            }
            catch (Exception ex)
            {
                _primaryException = ex;
                throw;
            }
        }

        /// <summary>
        /// 异步释放子流序列与受管操作，在 3 秒有限预算内汇聚生产者并安全清理。
        /// </summary>
        /// <returns>表示异步释放操作的任务。</returns>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var cancelTask = Task.Run(() =>
            {
                try { _linkedCancellation.Cancel(); }
                catch (ObjectDisposedException) { }
                catch { }
            });
            _ = cancelTask.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);

            while (_output.Reader.TryRead(out var pending))
            {
                pending.Consumed.TrySetCanceled(_linkedCancellation.Token);
            }

            using var cleanupBudgetCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var budgetToken = cleanupBudgetCts.Token;

            Exception? cleanupException = null;
            var cleanupTask = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(_producer, cancelTask).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    cleanupException = ex;
                }
                finally
                {
                    try { await _operation.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception opEx) { AbilityDiagnostics.Log("Warning", DiagnosticCategory, 0, $"子流 operation 释放异常: {AbilityDiagnostics.ExceptionSummary(opEx)}"); }

                    try { _linkedCancellation.Dispose(); }
                    catch (Exception ctsEx) { AbilityDiagnostics.Log("Warning", DiagnosticCategory, 0, $"子流取消源释放异常: {AbilityDiagnostics.ExceptionSummary(ctsEx)}"); }
                }
            });
            _session.TrackSessionWork(cleanupTask);

            var timedOut = false;
            try
            {
                await cleanupTask.WaitAsync(budgetToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (budgetToken.IsCancellationRequested && !cleanupTask.IsCompleted)
            {
                timedOut = true;
            }
            catch (Exception ex)
            {
                cleanupException ??= ex;
            }

            var hasOriginalCancellation = _cancellationToken.IsCancellationRequested || _enumerationCancellation.IsCancellationRequested;
            if (_primaryException is not null || hasOriginalCancellation)
            {
                if (timedOut)
                {
                    AbilityDiagnostics.Log("Warning", DiagnosticCategory, 0, "子流收尾超时，未能确认回收（保留原异常/取消）。");
                }
                else if (cleanupException is not null)
                {
                    AbilityDiagnostics.Log("Warning", DiagnosticCategory, 0, $"子流后台退出携带次级异常: {AbilityDiagnostics.ExceptionSummary(cleanupException)}");
                }
                return;
            }

            if (timedOut)
            {
                throw new InvalidOperationException("子流收尾超时，未能确认回收。");
            }

            if (_producer.IsFaulted && _producer.Exception is not null)
            {
                ExceptionDispatchInfo.Capture(_producer.Exception.GetBaseException()).Throw();
            }

            var isExpectedCancellation = cleanupException is OperationCanceledException
                && !hasOriginalCancellation
                && _primaryException is null;

            if (!isExpectedCancellation && cleanupException is not null)
            {
                ExceptionDispatchInfo.Capture(cleanupException).Throw();
            }

            if (_session.ChildRouter.HasFailedStops)
            {
                throw new InvalidOperationException("子调用未能确认回收。");
            }
        }
    }

    /// <summary>
    /// 流式子调用生产端后台任务。
    /// </summary>
    private static async Task<bool> ProduceStreamAsync<TInput, TOutput>(
        AbilitySession session,
        string launcherPath,
        TInput input,
        JsonTypeInfo<TInput> inputJsonTypeInfo,
        JsonTypeInfo<TOutput> outputJsonTypeInfo,
        ManagedOperationContext context,
        ChannelWriter<StreamOutput<TOutput>> output)
    {
        var inputFrame = IpcFrame.EncodeTaskInputFrame(
            session.TaskId,
            session.TaskTempPath,
            typeof(TInput).Name,
            input,
            inputJsonTypeInfo,
            AbilityTracing.CurrentTraceParent,
            AbilityTracing.CurrentTraceState);

        await using var child = await StartAsync(session, launcherPath, inputFrame, context.CancellationToken, context.TimeProvider);
        try
        {
            var first = await child.ReadDataAsync(context.CancellationToken);
            if (first.Type == ProtocolConstants.MessageTypes.SubmitFault)
            {
                var fault = ProtocolMessages.ReadBody<SubmitFaultBody>(first);
                throw new AbilityRequestFaultException(fault.Kind, fault.Message, fault.Site);
            }

            if (first.Type != ProtocolConstants.MessageTypes.ResultOpen)
            {
                throw new ProtocolException($"子 Launcher 流调用应先发送 ResultOpen，实际为：{first.Type}。");
            }

            var itemCount = 0;

            while (true)
            {
                var message = await child.ReadDataAsync(context.CancellationToken);
                if (message.Type == ProtocolConstants.MessageTypes.ResultItem)
                {
                    var item = ProtocolMessages.ReadBody<ResultItemBody>(message);
                    var value = JsonSerializer.Deserialize(item.Payload, outputJsonTypeInfo)
                        ?? throw new ProtocolException("子 Launcher 返回了空流元素。");

                    var consumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await output.WriteAsync(new StreamOutput<TOutput>(value, consumed), context.CancellationToken);
                    await consumed.Task.WaitAsync(context.CancellationToken);
                    itemCount++;

                    if (!child.ResultStreamClosed)
                    {
                        await child.WriteAsync(ProtocolMessages.CreateResultCredit(1), context.CancellationToken);
                    }

                    context.Progress.Report(new RunProgress { Stage = "ChildStream", Unit = "items", Completed = itemCount });
                    continue;
                }

                if (message.Type == ProtocolConstants.MessageTypes.ResultClose)
                {
                    await child.WriteAsync(ProtocolMessages.CreateResultClosed(itemCount), context.CancellationToken);
                    var reaped = await child.StopAndConfirmAsync("Completed");
                    if (reaped.ExitCode != 0)
                    {
                        throw new AbilityRequestFaultException("ChildExitCode", $"子 Launcher 流进程退出码为 {reaped.ExitCode?.ToString() ?? "unknown"}。");
                    }
                    return true;
                }

                if (message.Type == ProtocolConstants.MessageTypes.SubmitFault)
                {
                    var fault = ProtocolMessages.ReadBody<SubmitFaultBody>(message);
                    throw new AbilityRequestFaultException(fault.Kind, fault.Message, fault.Site);
                }

                throw new ProtocolException($"子 Launcher 流调用收到意外消息：{message.Type}。");
            }
        }
        catch (Exception primaryException)
        {
            ChildReapedBody? reaped = null;
            Exception? cleanupException = null;
            try
            {
                reaped = await child.StopAndConfirmAsync(context.CancellationToken.IsCancellationRequested ? "Cancelled" : "Faulted");
            }
            catch (Exception ex)
            {
                cleanupException = ex;
                primaryException.Data["ChildCleanupFailure"] = ex.ToString();
            }

            if (!context.CancellationToken.IsCancellationRequested
                && primaryException is not OperationCanceledException
                && primaryException is not AbilityRequestFaultException
                && reaped?.ExitCode is int exitCode && exitCode != 0)
            {
                throw AbilityRequestFaultException.Reviewed(
                    "ChildProcessExit",
                    $"子调用 {child.InvocationKey} 已退出，宿主确认 exit code {exitCode}。");
            }

            if (primaryException is OperationCanceledException)
            {
                if (cleanupException is not null)
                {
                    throw new InvalidOperationException($"子调用停止确认失败: {cleanupException.Message}", cleanupException);
                }

                if (reaped is null || !string.Equals(reaped.Outcome, "Reaped", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"子调用 {child.InvocationKey} 未能确认物理回收 (Outcome={reaped?.Outcome ?? "null"})。");
                }
            }

            throw;
        }
    }

    /// <summary>
    /// 将生产任务的结束或异常传递给消费通道的写入端。
    /// </summary>
    private static async Task CompleteStreamOutputAsync<T>(Task<bool> producer, ChannelWriter<StreamOutput<T>> writer)
    {
        Exception? failure = null;
        try
        {
            _ = await producer;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        writer.TryComplete(failure);
    }

    /// <summary>
    /// 单个流元素的包装记录，包含元素值与消费确认完成源。
    /// </summary>
    /// <param name="Value">流元素业务模型值。</param>
    /// <param name="Consumed">消费确认完成源通知句柄。</param>
    private sealed record StreamOutput<T>(T Value, TaskCompletionSource Consumed);

    /// <summary>
    /// 使用预先准备的 TaskInput 帧启动子 Launcher；握手成功后原样写出该帧。
    /// </summary>
    private static async Task<ChildSession> StartAsync(
        AbilitySession session,
        string launcherPath,
        byte[] inputFrame,
        CancellationToken cancellationToken,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherPath);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var invocationKey = "ci_" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(
            invocationKey,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var child = new ChildSession(session, invocationKey, pipe, DefaultInitialResultCredit);
        var buildTimedOut = false;
        var connectionTimedOut = false;
        var connected = false;

        try
        {
            using var startLimit = new CancellationTokenSource(session.BuildTimeout, timeProvider);
            using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                startLimit.Token);

            var fullLauncherPath = Path.GetFullPath(launcherPath);
            var registered = session.ChildRouter.RegisterChildSession(
                invocationKey,
                async () => { _ = await child.StopAndConfirmAsync("ParentEnded"); },
                child.SendControlAsync);
            if (!registered)
            {
                throw new ObjectDisposedException(nameof(ChildRouter), "会话子路由已关闭，拒绝启动新的子调用。");
            }

            child.MarkStartRequested();
            IpcEnvelope startResponse;
            try
            {
                startResponse = await session.RequestAsync(
                    ProtocolMessages.CreateChildStart(
                        session.NextRequestId(),
                        invocationKey,
                        session.InvocationKey,
                        session.TaskId,
                        fullLauncherPath),
                    startCancellation.Token);
            }
            catch (OperationCanceledException) when (
                startLimit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                buildTimedOut = true;
                throw AbilityRequestFaultException.Reviewed(
                    "ChildBuildTimeout",
                    $"子 Launcher 构建超过 {session.BuildTimeout}。");
            }

            if (startResponse.Type != ProtocolConstants.MessageTypes.ChildStarted)
            {
                var fault = ProtocolMessages.ReadBody<RequestFaultBody>(startResponse);
                throw new AbilityRequestFaultException(fault.Kind, fault.Message, "AbilityCall");
            }

            child.MarkStarted();

            var started = ProtocolMessages.ReadBody<ChildStartedBody>(startResponse);

            if (!string.Equals(started.InvocationKey, invocationKey, StringComparison.Ordinal)
                || !string.Equals(started.TaskId, session.TaskId, StringComparison.Ordinal))
            {
                throw new ProtocolException("ChildStarted 的调用键或 TaskId 与请求不匹配。");
            }

            using var connectionLimit = new CancellationTokenSource(session.ConnectionTimeout, timeProvider);
            using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                connectionLimit.Token);

            try
            {
                await pipe.WaitForConnectionAsync(connectionCancellation.Token);
                connected = true;

                await IpcFrame.WriteAsync(
                    pipe,
                    ProtocolMessages.CreateHello(
                        DefaultInitialResultCredit,
                        session.CleanupGracePeriod,
                        session.BuildTimeout,
                        session.ConnectionTimeout),
                    connectionCancellation.Token);

                var helloReply = await IpcFrame.ReadAsync(pipe, connectionCancellation.Token)
                    ?? throw new ProtocolException("子 SDK 在握手确认前断开。");

                if (helloReply.Type != ProtocolConstants.MessageTypes.HelloAccepted)
                {
                    throw new ProtocolException($"子 SDK 握手收到意外消息：{helloReply.Type}。");
                }

                var accepted = ProtocolMessages.ReadBody<HelloAcceptedBody>(helloReply);
                if (accepted.ProtocolMajor != ProtocolMessages.CurrentProtocolMajor)
                {
                    throw new ProtocolException("子 SDK 确认了不兼容的协议主版本。");
                }

                await pipe.WriteAsync(inputFrame, connectionCancellation.Token);
                await pipe.FlushAsync(connectionCancellation.Token);
            }
            catch (OperationCanceledException) when (
                connectionLimit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                connectionTimedOut = true;
                throw AbilityRequestFaultException.Reviewed(
                    "ChildConnectionTimeout",
                    $"子 Launcher 未能在 {session.ConnectionTimeout} 内完成连接与握手。");
            }

            child.StartReader();
            return child;
        }
        catch (Exception primaryException)
        {
            ChildReapedBody? reaped = null;
            var stopConfirmed = false;
            if (child.StartRequested)
            {
                try
                {
                    reaped = await child.StopAndConfirmAsync(
                        cancellationToken.IsCancellationRequested ? "Cancelled" : "StartFailed");
                    stopConfirmed = true;
                }
                catch (Exception cleanupException)
                {
                    primaryException.Data["ChildCleanupFailure"] = cleanupException.ToString();
                }
            }
            else
            {
                // 尚未请求启动，无在途子进程
                stopConfirmed = true;
            }

            if (stopConfirmed)
            {
                session.ChildRouter.RemoveChildSession(invocationKey);
            }
            try
            {
                await child.DisposeAsync();
            }
            catch (Exception cleanupException)
            {
                primaryException.Data["ChildDisposeFailure"] = cleanupException.ToString();
                AbilityDiagnostics.Log("Warning", DiagnosticCategory, 0, $"子调用启动失败后释放管道异常：{AbilityDiagnostics.ExceptionSummary(cleanupException)}");
            }

            if (!cancellationToken.IsCancellationRequested
                && !connected
                && (primaryException is not AbilityRequestFaultException requestFault
                    || requestFault.Kind == "ChildConnectionTimeout")
                && reaped?.ExitedNaturally == true
                && reaped?.ExitCode is int exitCode
                && exitCode != 0)
            {
                throw AbilityRequestFaultException.Reviewed(
                    "ChildProcessExit",
                    $"子 Launcher 在连接前退出，宿主确认 exit code {exitCode}。");
            }

            if (buildTimedOut && primaryException is not AbilityRequestFaultException)
            {
                throw AbilityRequestFaultException.Reviewed("ChildBuildTimeout", "子 Launcher 构建超时或失败。");
            }

            if (connectionTimedOut && primaryException is not AbilityRequestFaultException)
            {
                throw AbilityRequestFaultException.Reviewed("ChildConnectionTimeout", "子 Launcher 连接超时或失败。");
            }

            throw;
        }
    }

}
