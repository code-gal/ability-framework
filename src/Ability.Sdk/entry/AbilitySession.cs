using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Ability.Core;
using Ability.Model;

namespace Ability.Sdk;

/// <summary>
/// 负责单个任务进程内 IPC 会话全生命周期协调：
/// 解析命令行管道、建立双工连接、启动单一入站分发泵、执行 v3 协议握手、解码 TaskInput、注入执行环境、调用业务委托并提交一元或流式结果。
/// </summary>
internal sealed partial class AbilitySession : IAsyncDisposable
{
    /// <summary>
    /// 会话当前推进阶段。
    /// </summary>
    private enum SessionStage
    {
        /// <summary>
        /// 等待宿主发送握手 Hello 信封。
        /// </summary>
        WaitingHello = 0,
        /// <summary>
        /// 握手完成，等待宿主发送 TaskInput 任务输入信封。
        /// </summary>
        WaitingTaskInput = 1,
        /// <summary>
        /// 任务已启动，正在执行业务委托。
        /// </summary>
        Running = 2,
        /// <summary>
        /// 流式输出已结束并发送 ResultClose，等待宿主回发 ResultClosed 确认。
        /// </summary>
        StreamClosing = 3,
        /// <summary>
        /// 会话正常或故障结束。
        /// </summary>
        Completed = 4
    }

    /// <summary>
    /// 会话停止的原因归类。
    /// </summary>
    private enum SessionStopReason
    {
        /// <summary>
        /// 初始未停止状态。
        /// </summary>
        None = 0,
        /// <summary>
        /// 任务正常执行完成。
        /// </summary>
        Completed,
        /// <summary>
        /// 宿主发送 Cancel 或 Shutdown 主动取消。
        /// </summary>
        HostCancelled,
        /// <summary>
        /// 本地调用方传入的 CancellationToken 取消。
        /// </summary>
        CallerCancelled,
        /// <summary>
        /// 管道对端意外断开或入站流到达 EOF。
        /// </summary>
        Disconnected,
        /// <summary>
        /// 收到协议非法信封或违反应答时序。
        /// </summary>
        ProtocolError,
        /// <summary>
        /// 业务代码抛出未捕获异常。
        /// </summary>
        Fault
    }

    /// <summary>
    /// 结构化诊断记录类别。
    /// </summary>
    private const string DiagnosticCategory = "Ability.Sdk.Session";

    /// <summary>
    /// 命名管道客户端异步连接超时。
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 握手消息等待超时时限。
    /// </summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 流关闭确认消息等待超时时限（与宿主消费回调与排空时限对齐为 30 秒）。
    /// </summary>
    private static readonly TimeSpan ClosedConfirmTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 启动命令行参数。
    /// </summary>
    private readonly string[] _args;

    /// <summary>
    /// 底层命名管道客户端流。
    /// </summary>
    private NamedPipeClientStream? _pipeClient;

    /// <summary>
    /// 包装管道的 IPC 双工连接。
    /// </summary>
    private IpcConnection? _connection;

    /// <summary>
    /// 业务委托执行的取消源（可由宿主 Cancel 或网络断连触发）。
    /// </summary>
    private readonly CancellationTokenSource _businessCts = new();

    /// <summary>
    /// 入站分发泵循环的取消源。
    /// </summary>
    private readonly CancellationTokenSource _pumpCts = new();

    /// <summary>
    /// 会话关闭时通知正在排队等待网络应答的操作登记请求立即中止的取消源。
    /// </summary>
    private readonly CancellationTokenSource _registrationClosingCts = new();

    /// <summary>
    /// 终态通信阶段中断取消源（在宿主取消、连接意外断开或协议损坏时触发中断终态发送，与会话退出码解耦）。
    /// </summary>
    private readonly CancellationTokenSource _terminalInterruptCts = new();

    /// <summary>
    /// 入站分发泵后台异步任务。
    /// </summary>
    private Task? _inboundPumpTask;

    /// <summary>
    /// Hello 信封到达完成源。
    /// </summary>
    private readonly TaskCompletionSource<IpcEnvelope> _helloTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// TaskInput 信封到达完成源。
    /// </summary>
    private readonly TaskCompletionSource<IpcEnvelope> _taskInputTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// ResultClosed 信封到达完成源。
    /// </summary>
    private readonly TaskCompletionSource<IpcEnvelope> _resultClosedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 当前会话状态阶段。
    /// </summary>
    private SessionStage _stage = SessionStage.WaitingHello;

    /// <summary>
    /// 会话停止原因。
    /// </summary>
    private SessionStopReason _stopReason = SessionStopReason.None;

    /// <summary>
    /// Hello 信封到达防重原子计数器。
    /// </summary>
    private int _helloReceived;

    /// <summary>
    /// TaskInput 信封到达防重原子计数器。
    /// </summary>
    private int _taskInputReceived;

    /// <summary>
    /// 握手协商得到的流式初始额度窗口大小。
    /// </summary>
    private int _negotiatedCreditWindow;

    /// <summary>
    /// 握手协商得到的清理宽限期。
    /// </summary>
    private TimeSpan _cleanupGracePeriod = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 当前会话内登记的受管操作集合（键为 operationKey）。
    /// </summary>
    private readonly ConcurrentDictionary<string, ManagedOperation> _operations = new(StringComparer.Ordinal);

    /// <summary>
    /// 操作登记与会话收尾共同裁决同步互斥锁。
    /// </summary>
    private readonly object _operationsGate = new();

    /// <summary>
    /// 操作登记是否已关闭（会话进入收尾阶段后置为 true，拒绝任何新增操作登记）。
    /// </summary>
    private bool _operationsClosed;

    /// <summary>
    /// 待决完成登记的受管操作映射（用于消除 OperationAccepted 与 PolicyUpdate 间的竞态）。
    /// </summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ManagedOperation>> _pendingRegistrations = new(StringComparer.Ordinal);

    /// <summary>
    /// 受管资源租约管理器。
    /// </summary>
    private readonly LeaseManager _leaseManager = new();

    /// <summary>
    /// 会话环境级通用进度通道（32 阶段有界合并、自增序号与单一发送队列）。
    /// </summary>
    private ProgressChannel? _sessionProgressChannel;

    /// <summary>
    /// 子调用路由管理器。
    /// </summary>
    private readonly ChildRouter _childRouter = new();

    /// <summary>
    /// 当前任务唯一标识。
    /// </summary>
    private string _taskId = string.Empty;

    /// <summary>
    /// 任务专属临时目录绝对路径。
    /// </summary>
    private string _taskTempPath = string.Empty;

    /// <summary>
    /// 当前调用自身调用键（即根管道名称）。
    /// </summary>
    private string _invocationKey = string.Empty;

    /// <summary>
    /// 握手协商得到的构建超时时限。
    /// </summary>
    private TimeSpan _buildTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 握手协商得到的连接超时时限。
    /// </summary>
    private TimeSpan _connectionTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 资源释放标识状态（0 为未释放，1 为已释放）。
    /// </summary>
    private int _disposed;

    /// <summary>
    /// 初始化技能会话。
    /// </summary>
    /// <param name="args">技能进程启动命令行参数。</param>
    public AbilitySession(string[] args)
    {
        _args = args ?? throw new ArgumentNullException(nameof(args));
    }

    /// <summary>
    /// 执行一元模式任务全流程。
    /// </summary>
    public async Task<int> RunUnaryAsync<TInput, TOutput>(
        Func<TInput, ExecutionContextMdl, Task<TOutput>> handler,
        JsonTypeInfo<TInput> inputJsonTypeInfo,
        JsonTypeInfo<TOutput> outputJsonTypeInfo,
        string? outputPayloadType,
        CancellationToken cancellationToken)
    {
        return await RunCoreAsync(
            async (input, contextMdl, connection) =>
            {
                var output = await AbilityEnvironment.RunAsync(contextMdl, () => handler(input, contextMdl)).ConfigureAwait(false);
                var typeName = outputPayloadType ?? typeof(TOutput).Name;

                // 必要收尾在发送终态前执行；确认在途业务停止后再释放租约并排空
                var teardownSuccess = await EnsureTeardownAsync().ConfigureAwait(false);
                if (!teardownSuccess)
                {
                    throw new InvalidOperationException("会话收尾超时或租约释放失败。");
                }

                // 终态发送与业务取消解耦，受自身合理时限（5秒）、调用方取消与宿主取消约束
                using var submitTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linkedSubmitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _terminalInterruptCts.Token, submitTimeoutCts.Token);
                await connection.SendSubmitResultAsync(typeName, output, outputJsonTypeInfo, linkedSubmitCts.Token).ConfigureAwait(false);
            },
            inputJsonTypeInfo,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 执行流式模式任务全流程。
    /// </summary>
    public async Task<int> RunStreamAsync<TInput, TOutput>(
        Func<TInput, ExecutionContextMdl, IAsyncEnumerable<TOutput>> handler,
        JsonTypeInfo<TInput> inputJsonTypeInfo,
        JsonTypeInfo<TOutput> outputJsonTypeInfo,
        CancellationToken cancellationToken)
    {
        return await RunCoreAsync(
            async (input, contextMdl, connection) =>
            {
                var payloadType = typeof(TOutput).Name;
                // 1. 发送开启流声明
                await connection.SendAsync(ProtocolMessages.CreateResultOpen(payloadType), contextMdl.CancellationToken).ConfigureAwait(false);
                connection.StreamState.MarkOpen();

                var stream = AbilityEnvironment.RunAsync(contextMdl, () => handler(input, contextMdl));
                var enumerator = stream.GetAsyncEnumerator(contextMdl.CancellationToken);

                Exception? streamException = null;
                var streamEndedNormally = false;

                // 互斥门控与在途 MoveNextAsync 任务引用，确保 MoveNextAsync 与 DisposeAsync 绝不并发
                var moveNextLock = new object();
                Task<bool>? inFlightMoveNext = null;

                try
                {
                    while (true)
                    {
                        // 2. 生产前先获取 1 点额度，严格实现背压控制
                        await connection.StreamState.AcquireCreditAsync(contextMdl.CancellationToken).ConfigureAwait(false);

                        // 3. 获得额度后再拉取下一项：隔离业务推进的同步阻塞并取得生命周期所有权，调用方等待可响应停止信号
                        Task<bool> moveNextTask;
                        lock (moveNextLock)
                        {
                            moveNextTask = Task.Run(async () =>
                            {
                                var vt = enumerator.MoveNextAsync();
                                return vt.IsCompleted ? vt.Result : await vt.ConfigureAwait(false);
                            });
                            inFlightMoveNext = moveNextTask;
                        }
                        TrackSessionWork(moveNextTask);

                        bool hasMore;
                        try
                        {
                            hasMore = await moveNextTask.WaitAsync(contextMdl.CancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            lock (moveNextLock)
                            {
                                if (inFlightMoveNext == moveNextTask && moveNextTask.IsCompleted)
                                {
                                    inFlightMoveNext = null;
                                }
                            }
                        }

                        if (!hasMore)
                        {
                            // 流已结束，归还本次预扣的 1 点额度
                            connection.StreamState.ReturnCredit(1);
                            streamEndedNormally = true;
                            break;
                        }

                        var item = enumerator.Current;
                        await connection.SendResultItemAsync(item, outputJsonTypeInfo, contextMdl.CancellationToken).ConfigureAwait(false);
                        connection.StreamState.RecordItem();
                    }
                }
                catch (Exception ex)
                {
                    streamException = ex;
                    throw;
                }
                finally
                {
                    // 统一 3 秒整体预算，枚举器清理、停止后在途推进收拢及必要收尾共享此时限，后续步骤使用剩余时间，不重新开始计时
                    using var sharedBudgetCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var budgetToken = sharedBudgetCts.Token;

                    var cleanupSuccess = await CleanUpEnumeratorAndTeardownAsync(
                        enumerator,
                        moveNextLock,
                        inFlightMoveNext,
                        budgetToken).ConfigureAwait(false);

                    if (streamEndedNormally && streamException is null)
                    {
                        if (!cleanupSuccess)
                        {
                            throw new InvalidOperationException("流枚举器收尾超时或会话租约释放失败。");
                        }

                        _stage = SessionStage.StreamClosing;
                        // 流关闭通知与确认等待与业务取消解耦，受自身合理时限、调用方取消与宿主取消约束
                        using var closeTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        using var linkedCloseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _terminalInterruptCts.Token, closeTimeoutCts.Token);
                        await connection.SendAsync(ProtocolMessages.CreateResultClose(), linkedCloseCts.Token).ConfigureAwait(false);

                        using var closedTimeoutCts = new CancellationTokenSource(ClosedConfirmTimeout);
                        using var linkedClosedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _terminalInterruptCts.Token, closedTimeoutCts.Token);

                        var closedEnvelope = await _resultClosedTcs.Task.WaitAsync(linkedClosedCts.Token).ConfigureAwait(false);
                        var closed = ProtocolMessages.ReadBody<ResultClosedBody>(closedEnvelope);
                        connection.StreamState.MarkClosed(closed.ItemCount);
                    }
                }
            },
            inputJsonTypeInfo,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 会话公共核心执行流：连接、握手、接收参数、执行委托并处理异常收尾。
    /// </summary>
    /// <typeparam name="TInput">业务输入参数类型。</typeparam>
    /// <param name="executeAction">具体执行委托。</param>
    /// <param name="inputJsonTypeInfo">输入参数的 JSON 类型元数据。</param>
    /// <param name="cancellationToken">外部取消令牌。</param>
    /// <returns>进程退出码（0 成功，1 错误，130 取消）。</returns>
    private async Task<int> RunCoreAsync<TInput>(
        Func<TInput, ExecutionContextMdl, IpcConnection, Task> executeAction,
        JsonTypeInfo<TInput> inputJsonTypeInfo,
        CancellationToken cancellationToken)
    {
        if (!SessionCommandLine.TryGetPipeName(_args, out var pipeName, out var argError))
        {
            await Console.Error.WriteLineAsync(argError).ConfigureAwait(false);
            return 1;
        }

        using var linkedCallerCts = CancellationTokenSource.CreateLinkedTokenSource(_businessCts.Token, cancellationToken);
        var token = linkedCallerCts.Token;

        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _stopReason = SessionStopReason.CallerCancelled;
                return 130;
            }

            _invocationKey = pipeName;

            // 1. 建立管道客户端连接
            _pipeClient = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            using var connectCts = new CancellationTokenSource(ConnectTimeout);
            using var linkedConnectCts = CancellationTokenSource.CreateLinkedTokenSource(connectCts.Token, token);
            await _pipeClient.ConnectAsync(linkedConnectCts.Token).ConfigureAwait(false);

            var connection = new IpcConnection(_pipeClient);
            connection.LateReplyHandler = envelope =>
            {
                // 迟到租约批准回复单一处理者：由租约管理器登记释放任务并纳入会话收尾
                _leaseManager.HandleLateLeaseGranted(envelope, connection);
            };
            _connection = connection;
            connection.Start();
            _sessionProgressChannel = new ProgressChannel(
                (envelope, ct) => connection.SendAsync(envelope, ct),
                string.Empty,
                TimeSpan.FromMilliseconds(250),
                TimeProvider.System);

            // 启动单一入站分发泵
            _inboundPumpTask = RunInboundPumpAsync(connection, _pumpCts.Token);

            // 2. 协议握手
            _stage = SessionStage.WaitingHello;
            using var handshakeCts = new CancellationTokenSource(HandshakeTimeout);
            using var linkedHandshakeCts = CancellationTokenSource.CreateLinkedTokenSource(handshakeCts.Token, token);
            var helloEnvelope = await _helloTcs.Task.WaitAsync(linkedHandshakeCts.Token).ConfigureAwait(false);
            var hello = ProtocolMessages.ReadBody<HelloBody>(helloEnvelope);

            if (hello.ProtocolMajor != ProtocolConstants.CurrentProtocolMajor)
            {
                _stopReason = SessionStopReason.ProtocolError;
                using var mismatchCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _connection.SendAsync(
                    ProtocolMessages.CreateProtocolMismatch(ProtocolConstants.CurrentProtocolMajor, hello.ProtocolMajor),
                    mismatchCts.Token).ConfigureAwait(false);
                return 1;
            }

            if (hello.ResultCreditWindow < 1
                || hello.ResultCreditWindow > 65536
                || hello.CleanupGracePeriod <= TimeSpan.Zero
                || hello.ConnectionTimeout <= TimeSpan.Zero
                || hello.BuildTimeout <= TimeSpan.Zero)
            {
                _stopReason = SessionStopReason.ProtocolError;
                throw new ProtocolException("Hello 中的时限或窗口参数非法。");
            }

            // 保存宿主协商参数
            _negotiatedCreditWindow = hello.ResultCreditWindow;
            _cleanupGracePeriod = hello.CleanupGracePeriod;
            _buildTimeout = hello.BuildTimeout;
            _connectionTimeout = hello.ConnectionTimeout;

            // 在回发 HelloAccepted 之前，先将阶段迁移为 WaitingTaskInput，杜绝快速 TaskInput 与主流程竞争
            _stage = SessionStage.WaitingTaskInput;

            // 回发 HelloAccepted
            using var acceptCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _connection.SendAsync(ProtocolMessages.CreateHelloAccepted(), acceptCts.Token).ConfigureAwait(false);

            // 初始化流式额度
            if (_negotiatedCreditWindow > 0)
            {
                _connection.StreamState.GrantCredit(_negotiatedCreditWindow);
            }

            // 3. 接收 TaskInput
            var inputEnvelope = await _taskInputTcs.Task.WaitAsync(token).ConfigureAwait(false);
            var taskInput = ProtocolMessages.ReadBody<TaskInputBody>(inputEnvelope);

            if (string.IsNullOrWhiteSpace(taskInput.TaskTempPath) || !Path.IsPathFullyQualified(taskInput.TaskTempPath))
            {
                _stopReason = SessionStopReason.ProtocolError;
                throw new ProtocolException("TaskInput 中的 TaskTempPath 必须为有效的绝对路径。");
            }

            _taskId = taskInput.TaskId;
            _taskTempPath = taskInput.TaskTempPath;
            AbilityTracing.Start(inputEnvelope.TraceParent, inputEnvelope.TraceState, "AbilityTask");

            var inputModel = JsonSerializer.Deserialize(taskInput.Payload, inputJsonTypeInfo)
                ?? throw new ProtocolException("TaskInput 业务输入载荷为空或无法反序列化。");

            // 4. 构造执行环境并执行业务委托
            _stage = SessionStage.Running;
            var contextMdl = new ExecutionContextMdl
            {
                CancellationToken = token,
                TimeProvider = TimeProvider.System,
                Progress = new SessionProgress(this)
            };

            using (AbilityRuntime.Enter(this))
            using (AbilityEnvironment.EnterTaskTempPath(taskInput.TaskTempPath))
            {
                await executeAction(inputModel, contextMdl, _connection).ConfigureAwait(false);
            }

            // 5. 优雅收尾：统一确保收尾已执行，再关闭通信
            await EnsureTeardownAsync().ConfigureAwait(false);
            _stage = SessionStage.Completed;
            _stopReason = SessionStopReason.Completed;
            await _connection.CloseAsync(IpcTerminationReason.NormalClosed).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _stopReason == SessionStopReason.HostCancelled || _stopReason == SessionStopReason.CallerCancelled)
        {
            // 调用方或宿主主动取消：统一先阻止新工作、收尾受管操作、排空租约释放，再按 130 退出，严禁发送 SubmitFault 或 ResultClose
            await EnsureTeardownAsync().ConfigureAwait(false);
            if (_connection is not null && !_connection.IsClosed)
            {
                try { await _connection.CloseAsync(IpcTerminationReason.CallerCancelled).ConfigureAwait(false); } catch { }
            }
            return 130;
        }
        catch (Exception ex)
        {
            // 故障路径（包含断连、协议损坏与业务异常）：统一先阻止新工作、收尾受管操作、排空租约释放，再发送 SubmitFault 并关闭通信
            await EnsureTeardownAsync().ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested || _stopReason == SessionStopReason.HostCancelled || _stopReason == SessionStopReason.CallerCancelled)
            {
                return 130;
            }

            if (_stopReason == SessionStopReason.None)
            {
                _stopReason = ex is ProtocolException ? SessionStopReason.ProtocolError : SessionStopReason.Fault;
            }

            try
            {
                AbilityDiagnostics.Log("Warning", DiagnosticCategory, 0, $"[Ability.Sdk] 执行故障: {AbilityDiagnostics.ExceptionSummary(ex)}");
            }
            catch
            {
            }

            if (_connection is not null && !_connection.IsClosed)
            {
                try
                {
                    var (faultKind, faultMessage, faultSite) = SessionFaultExtractor.Extract(ex);
                    using var faultCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _connection.SendAsync(
                        ProtocolMessages.CreateSubmitFault(faultKind, faultMessage, site: faultSite),
                        faultCts.Token).ConfigureAwait(false);

                    // 业务故障不等于连接协议损坏，正常关闭连接
                    var closeReason = ex is ProtocolException
                        ? IpcTerminationReason.ProtocolCorrupted
                        : IpcTerminationReason.NormalClosed;
                    await _connection.CloseAsync(closeReason).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            return 1;
        }
        finally
        {
            _pumpCts.Cancel();

            if (_inboundPumpTask is not null)
            {
                try
                {
                    await _inboundPumpTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            AbilityTracing.Stop(_stopReason == SessionStopReason.Completed, _stopReason == SessionStopReason.Completed ? null : _stopReason.ToString());
            using (var childTeardownCts = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
            {
                try { await _childRouter.DisposeAsync(childTeardownCts.Token).ConfigureAwait(false); } catch { }
            }
            try { await AbilityDiagnostics.ShutdownAsync().ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>
    /// 会话唯一的入站消息分发泵：负责分发输入、额度和关闭确认，并将停止与断连传入会话取消源。
    /// </summary>
    private async Task RunInboundPumpAsync(IpcConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            while (await connection.Inbound.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (connection.Inbound.TryRead(out var envelope))
                {
                    await DispatchInboundEnvelopeAsync(connection, envelope, cancellationToken).ConfigureAwait(false);
                }
            }

            // 入站通道到达 EOF 且连接结束
            if (_stopReason == SessionStopReason.None)
            {
                _stopReason = SessionStopReason.Disconnected;
            }
            try { _terminalInterruptCts.Cancel(); } catch { }
            _ = TriggerBusinessCancellation();
        }
        catch (OperationCanceledException)
        {
            // 正常停止泵
        }
        catch (Exception ex)
        {
            if (_stopReason == SessionStopReason.None)
            {
                _stopReason = ex is ProtocolException ? SessionStopReason.ProtocolError : SessionStopReason.Disconnected;
            }

            try { _terminalInterruptCts.Cancel(); } catch { }
            _helloTcs.TrySetException(ex);
            _taskInputTcs.TrySetException(ex);
            _resultClosedTcs.TrySetException(ex);
            _ = TriggerBusinessCancellation();
        }
        finally
        {
            _helloTcs.TrySetCanceled();
            _taskInputTcs.TrySetCanceled();
            _resultClosedTcs.TrySetCanceled();
        }
    }

    /// <summary>
    /// 将从入站通道读取到的单条信封根据会话阶段分发到对应的处理逻辑。
    /// </summary>
    /// <param name="connection">当前 IPC 连接。</param>
    /// <param name="envelope">入站消息信封。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task DispatchInboundEnvelopeAsync(IpcConnection connection, IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        switch (_stage)
        {
            case SessionStage.WaitingHello:
                if (IsCancelOrShutdown(envelope.Type))
                {
                    _stopReason = SessionStopReason.HostCancelled;
                    try { _terminalInterruptCts.Cancel(); } catch { }
                    _ = TriggerBusinessCancellation();
                    return;
                }

                if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.Hello, StringComparison.Ordinal))
                {
                    if (Interlocked.Exchange(ref _helloReceived, 1) != 0)
                    {
                        throw new ProtocolException("握手期间收到重复的 Hello 信封。");
                    }
                    _helloTcs.TrySetResult(envelope);
                    return;
                }

                await HandleUnexpectedEnvelopeAsync(connection, envelope, cancellationToken).ConfigureAwait(false);
                break;

            case SessionStage.WaitingTaskInput:
                if (IsCancelOrShutdown(envelope.Type))
                {
                    _stopReason = SessionStopReason.HostCancelled;
                    try { _terminalInterruptCts.Cancel(); } catch { }
                    _ = TriggerBusinessCancellation();
                    return;
                }

                if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.Hello, StringComparison.Ordinal))
                {
                    throw new ProtocolException("握手完成后收到非预期的重复 Hello 信封。");
                }

                if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.TaskInput, StringComparison.Ordinal))
                {
                    if (Interlocked.Exchange(ref _taskInputReceived, 1) != 0)
                    {
                        throw new ProtocolException("初始化期间收到重复的 TaskInput 信封。");
                    }
                    _taskInputTcs.TrySetResult(envelope);
                    return;
                }

                await HandleUnexpectedEnvelopeAsync(connection, envelope, cancellationToken).ConfigureAwait(false);
                break;

            case SessionStage.Running:
            case SessionStage.StreamClosing:
                if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.Hello, StringComparison.Ordinal)
                    || string.Equals(envelope.Type, ProtocolConstants.MessageTypes.TaskInput, StringComparison.Ordinal))
                {
                    throw new ProtocolException($"任务运行期间收到非预期的重复 {envelope.Type} 信封。");
                }

                if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.ResultCredit, StringComparison.Ordinal))
                {
                    var credit = ProtocolMessages.ReadBody<ResultCreditBody>(envelope);
                    if (credit.Count <= 0)
                    {
                        throw new ProtocolException($"ResultCredit.Count 必须大于 0，实际收到：{credit.Count}。");
                    }
                    connection.StreamState.GrantCredit(credit.Count);
                    return;
                }

                if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.ResultClosed, StringComparison.Ordinal))
                {
                    if (_stage != SessionStage.StreamClosing)
                    {
                        throw new ProtocolException("在发送 ResultClose 之前提前收到 ResultClosed 属于协议违规。");
                    }
                    _resultClosedTcs.TrySetResult(envelope);
                    return;
                }

                if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.PolicyUpdate, StringComparison.Ordinal))
                {
                    _ = HandlePolicyUpdateAsync(connection, envelope);
                    return;
                }

                if (string.Equals(envelope.Type, ProtocolConstants.MessageTypes.ChildMessage, StringComparison.Ordinal))
                {
                    _childRouter.ForwardChildControl(envelope);
                    return;
                }

                if (IsCancelOrShutdown(envelope.Type))
                {
                    _stopReason = SessionStopReason.HostCancelled;
                    try { _terminalInterruptCts.Cancel(); } catch { }
                    _ = TriggerBusinessCancellation();
                    return;
                }

                await HandleUnexpectedEnvelopeAsync(connection, envelope, cancellationToken).ConfigureAwait(false);
                break;

            case SessionStage.Completed:
            default:
                break;
        }
    }

    /// <summary>
    /// 异步处理宿主推送的动态策略变更信封。
    /// 独立调度执行以避免阻塞入站分发泵（确保 Cancel 能及时分发）；若操作正处于待决登记中，等待其就绪消除竞态。
    /// </summary>
    /// <param name="connection">当前 IPC 连接。</param>
    /// <param name="envelope">PolicyUpdate 协议信封。</param>
    private async Task HandlePolicyUpdateAsync(IpcConnection connection, IpcEnvelope envelope)
    {
        try
        {
            if (envelope.Id is not long requestId)
            {
                return;
            }

            var update = ProtocolMessages.ReadBody<PolicyUpdateBody>(envelope);
            ManagedOperation? op = null;
            if (!_operations.TryGetValue(update.OperationKey, out op))
            {
                if (_pendingRegistrations.TryGetValue(update.OperationKey, out var pendingTcs))
                {
                    try
                    {
                        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        op = await pendingTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }

            using var replyCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (op is not null)
            {
                var (outcome, version) = op.ApplyPolicy(update.Policy, update.Version, update.Source);
                await connection.SendAsync(
                    ProtocolMessages.CreatePolicyApplied(requestId, update.OperationKey, version, update.Source, outcome),
                    replyCts.Token).ConfigureAwait(false);
            }
            else
            {
                await connection.SendAsync(
                    ProtocolMessages.CreateRequestFault(requestId, "OperationNotFound", $"未找到键为 '{update.OperationKey}' 的活跃受管操作。"),
                    replyCts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // 策略更新确认发送失败由底层连接记录，不影响会话
        }
    }

    /// <summary>
    /// 检查信封类型是否为宿主发起的取消或关机指令。
    /// </summary>
    /// <param name="type">消息类型标识。</param>
    /// <returns>若为 Cancel 或 Shutdown 返回 true；否则返回 false。</returns>
    private static bool IsCancelOrShutdown(string type)
        => string.Equals(type, ProtocolConstants.MessageTypes.Cancel, StringComparison.Ordinal)
           || string.Equals(type, ProtocolConstants.MessageTypes.Shutdown, StringComparison.Ordinal);

    /// <summary>
    /// 处理当前阶段非预期的信封：未知请求回发 UnsupportedRequest，未知必选通知抛出协议异常。
    /// </summary>
    /// <param name="connection">当前 IPC 连接。</param>
    /// <param name="envelope">待处理的信封。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task HandleUnexpectedEnvelopeAsync(IpcConnection connection, IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.Id is long requestId)
        {
            // 未知请求：主动回发 UnsupportedRequest 答复，防止调用方永久挂起
            using var replyCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var linkedReplyCts = CancellationTokenSource.CreateLinkedTokenSource(replyCts.Token, cancellationToken);
            await connection.SendAsync(
                ProtocolMessages.CreateRequestFault(
                    requestId,
                    "UnsupportedRequest",
                    $"当前阶段 {_stage} 不支持请求类型 '{envelope.Type}'。"),
                linkedReplyCts.Token).ConfigureAwait(false);
            return;
        }

        // 未知通知：若为可选通知则忽略，若为必选通知则归为协议错误
        if (envelope.OptionalNotification)
        {
            return;
        }

        throw new ProtocolException($"收到当前阶段 {_stage} 不支持的必选通知消息 '{envelope.Type}'。");
    }

    /// <summary>
    /// 获取当前可用的双工通信连接实例；若未建立连接则抛出异常。
    /// </summary>
    private IpcConnection RequireConnection()
        => _connection ?? throw new InvalidOperationException("当前会话尚未建立底层 IPC 双工通信连接。");

    /// <summary>
    /// 向宿主申请指定受管资源的访问租约。
    /// </summary>
    /// <summary>
    /// 获取当前会话关联的任务唯一标识符。
    /// </summary>
    internal string TaskId => _taskId;

    /// <summary>
    /// 获取宿主为本次任务分配的专属临时目录绝对路径。
    /// </summary>
    internal string TaskTempPath => _taskTempPath;

    /// <summary>
    /// 获取当前调用自身的调用键。
    /// </summary>
    internal string InvocationKey => _invocationKey;

    /// <summary>
    /// 获取子调用启动构建超时时限。
    /// </summary>
    internal TimeSpan BuildTimeout => _buildTimeout;

    /// <summary>
    /// 获取子调用管道连接与握手超时时限。
    /// </summary>
    internal TimeSpan ConnectionTimeout => _connectionTimeout;

    /// <summary>
    /// 获取清理宽限期。
    /// </summary>
    internal TimeSpan CleanupGracePeriod => _cleanupGracePeriod;

    /// <summary>
    /// 获取业务执行取消令牌。
    /// </summary>
    internal CancellationToken BusinessCancellationToken => _businessCts.Token;

    /// <summary>
    /// 获取子调用路由管理器实例。
    /// </summary>
    internal ChildRouter ChildRouter => _childRouter;

    /// <summary>
    /// 生成下一个子调用请求流水号（统一委托至底层连接）。
    /// </summary>
    internal long NextRequestId() => RequireConnection().NextRequestId();

    /// <summary>
    /// 向宿主异步发送请求并等待应答。
    /// </summary>
    internal Task<IpcEnvelope> RequestAsync(IpcEnvelope envelope, CancellationToken ct) => RequireConnection().RequestAsync(envelope, ct);

    /// <summary>
    /// 向宿主申请指定受管资源的访问租约。
    /// </summary>
    internal Task<AbilityLease> LeaseAsync(string resource, LeaseMode mode, CancellationToken cancellationToken)
        => _leaseManager.AcquireLeaseAsync(RequireConnection(), resource, mode, _businessCts.Token, cancellationToken);

    /// <summary>
    /// 向宿主汇报操作或会话执行进度。
    /// </summary>
    /// <param name="operationKey">操作键；为空表示会话级通用进度。</param>
    /// <param name="progress">进度模型实例。</param>
    internal Task ReportProgressAsync(string operationKey, RunProgress progress)
    {
        if (!string.IsNullOrEmpty(operationKey))
        {
            if (_operations.TryGetValue(operationKey, out var op))
            {
                op.ReportProgress(progress);
            }
            return Task.CompletedTask;
        }

        _sessionProgressChannel?.Report(progress);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 向宿主申请补充文本参数。
    /// </summary>
    internal Task<string> RequestParamAsync(ParamRequestMdl request, CancellationToken cancellationToken)
        => ParameterComplementation.RequestParamAsync(RequireConnection(), request, cancellationToken);

    /// <summary>
    /// 向宿主申请补充强类型结构化参数。
    /// </summary>
    internal Task<T> RequestParamAsync<T>(ParamRequestMdl request, JsonTypeInfo<T> jsonTypeInfo, CancellationToken cancellationToken)
        => ParameterComplementation.RequestParamAsync(RequireConnection(), request, jsonTypeInfo, cancellationToken);

    /// <summary>
    /// 向宿主发起数据源失效恢复请求。
    /// </summary>
    internal Task<SourceRepairOutcome> RequestSourceRepairAsync(
        string sourceKind, string sourceRef, string failureReason, bool hotUpdateSupported, CancellationToken cancellationToken)
        => ParameterComplementation.RequestSourceRepairAsync(
            RequireConnection(), sourceKind, sourceRef, failureReason, hotUpdateSupported, cancellationToken);
}
