using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// 单次运行对象的启动与执行阶段：启动根进程、建立连接、握手、下发输入，并移交执行协调器。
/// </summary>
internal sealed partial class HostRun
{
    /// <summary>本次运行的执行与关联取消令牌。</summary>
    private CancellationToken ExecutionToken => _linkedCts!.Token;

    /// <summary>
    /// 建立子调用管理器，并接入进程启动登记与自然退出回收回调。
    /// </summary>
    private void StartChildCallManager()
    {
        _childCalls = new HostChildCalls(
            Registry,
            _options.BuildTimeout,
            _options.CancelGracePeriod,
            _cancellationToken,
            _options.TimeProvider,
            diagnostic: msg => Dispatcher.DispatchDiagnostic(msg),
            processStarted: pid => _tempDirectory!.RecordProcessId(pid),
            processExited: RegisterNaturalExitReap);
    }

    /// <summary>
    /// 登记子进程自然退出后的子树回收任务，并收集其迟到异常。
    /// </summary>
    /// <param name="record">自然退出的调用记录。</param>
    private void RegisterNaturalExitReap(InvocationRecord record)
    {
        lock (_naturalExitLock)
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    var subtree = Registry.RequestStopSubtree(record.InvocationKey);
                    if (_childCalls is not null)
                    {
                        var revoked = await _childCalls.ReapExitedTreeAsync(subtree).ConfigureAwait(false);
                        Governance.RevokeLeases(revoked);
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.DispatchDiagnostic($"自然退出回收子树异常: {DescribeSafeError(ex)}");
                    throw;
                }
            });

            _naturalExitTasks.Add(task);

            _ = task.ContinueWith(
                completed =>
                {
                    if (completed.IsFaulted && completed.Exception is not null)
                    {
                        var baseEx = completed.Exception.GetBaseException();
                        lock (_naturalExitLock)
                        {
                            _naturalExitIssues.Add(new HostCleanupIssue("NaturalExitTask", baseEx.GetType().Name, DescribeSafeError(baseEx)));
                        }
                    }

                    lock (_naturalExitLock)
                    {
                        _naturalExitTasks.Remove(completed);
                    }
                },
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// 启动根进程、建立连接、完成握手与输入下发，随后把消息循环交给执行协调器。
    /// 根进程启动失败时直接进入前台收尾。
    /// 进入本阶段即标记根记录已进入启动流程，此后由进程护栏按真实启动结果回收。
    /// </summary>
    /// <returns>启动与执行的异步完成任务。</returns>
    private async Task LaunchAndExecuteAsync()
    {
        _rootLaunchEntered = true;

        if (!await StartRootProcessAsync().ConfigureAwait(false))
        {
            return;
        }

        CreateExecutionCancellationSources();
        await WaitForConnectionAsync().ConfigureAwait(false);
        CreateConnection();
        await PerformHandshakeAsync().ConfigureAwait(false);
        await SendTaskInputAsync().ConfigureAwait(false);
        await RunCoordinatorAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 启动根调用子进程并把 PID 登记入任务专属目录，随后完成启动收口。
    /// </summary>
    /// <returns>根进程启动并完成登记时为 true。</returns>
    private async Task<bool> StartRootProcessAsync()
    {
        try
        {
            await ProcessGuard.StartAsync(
                RootRecord,
                _launcher!,
                _invocation.Arguments,
                _options.CancelGracePeriod,
                _invocation.EnvironmentVariables).ConfigureAwait(false);

            if (RootRecord.ProcessId is int rootPid)
            {
                try
                {
                    _tempDirectory!.RecordProcessId(rootPid);
                    _launchSucceeded = true;
                }
                catch (TaskTempOwnershipException ex)
                {
                    _outcome = "TaskTempMarkerError";
                    _faultKind = "TaskTempMarkerError";
                    _faultMessage = ex.Message;
                }
            }
            else
            {
                _launchSucceeded = true;
            }
        }
        catch (Exception ex)
        {
            _outcome = "ProcessCrash";
            _faultKind = "ProcessStartFailed";
            _faultMessage = $"启动根调用子进程失败: {ex.GetType().Name}";
        }
        finally
        {
            RootRecord.CompleteLaunch();
        }

        return _launchSucceeded;
    }

    /// <summary>
    /// 建立整任务执行期限取消源及其与调用方令牌的关联。
    /// </summary>
    private void CreateExecutionCancellationSources()
    {
        _executionCts = _options.ExecutionTimeout is TimeSpan execTimeout
            ? new CancellationTokenSource(execTimeout, _options.TimeProvider)
            : new CancellationTokenSource();
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_executionCts.Token, _cancellationToken);
    }

    /// <summary>
    /// 等待技能进程建立命名管道连接；子进程提前退出与连接超时分别归类。
    /// </summary>
    /// <returns>等待连接的异步完成任务。</returns>
    private async Task WaitForConnectionAsync()
    {
        using var connectCts = new CancellationTokenSource(_options.ConnectTimeout, _options.TimeProvider);
        using var linkedConnectCts = CancellationTokenSource.CreateLinkedTokenSource(connectCts.Token, ExecutionToken);

        var connectTask = _pipeServer!.WaitForConnectionAsync(linkedConnectCts.Token);
        var completedTask = await Task.WhenAny(connectTask, RootRecord.ProcessExitTask ?? Task.CompletedTask).ConfigureAwait(false);

        if (completedTask != connectTask)
        {
            _outcome = "ProcessExitBeforeConnect";
            _faultKind = "ProcessPrematureExit";
            _faultMessage = $"子进程在建立 IPC 管道连接前已提前退出，退出码：{RootRecord.ExitCode}。";
            throw new InvalidOperationException(_faultMessage);
        }

        try
        {
            await connectTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (connectCts.IsCancellationRequested && !ExecutionToken.IsCancellationRequested)
        {
            _connectTimedOut = true;
            throw;
        }
    }

    /// <summary>
    /// 用已连接的命名管道建立 IPC 连接并启动收发循环。
    /// </summary>
    private void CreateConnection()
    {
        var inboundCapacity = Math.Max(128, _options.ResultCreditWindow + _options.MaxPendingControlFrames + 64);
        _connection = new IpcConnection(
            _pipeServer!,
            initialCreditWindow: _options.ResultCreditWindow,
            maxControlFrames: _options.MaxPendingControlFrames,
            inboundCapacity: inboundCapacity);
        _connection.Start();
    }

    /// <summary>
    /// 执行 v3 握手：发送 Hello 并核验 HelloAccepted 的协议主版本。
    /// </summary>
    /// <returns>握手交互的异步完成任务。</returns>
    private async Task PerformHandshakeAsync()
    {
        using var handshakeCts = new CancellationTokenSource(_options.HandshakeTimeout, _options.TimeProvider);
        using var linkedHandshakeCts = CancellationTokenSource.CreateLinkedTokenSource(handshakeCts.Token, ExecutionToken);

        try
        {
            await _connection!.SendAsync(
                ProtocolMessages.CreateHello(
                    _options.ResultCreditWindow,
                    _options.CancelGracePeriod,
                    buildTimeout: _options.BuildTimeout,
                    connectionTimeout: _options.ConnectTimeout),
                linkedHandshakeCts.Token).ConfigureAwait(false);

            var handshakeEnvelope = await ReadNextEnvelopeAsync(_connection, linkedHandshakeCts.Token).ConfigureAwait(false);
            RecordObservedMessage(handshakeEnvelope.Type);

            if (string.Equals(handshakeEnvelope.Type, ProtocolConstants.MessageTypes.ProtocolMismatch, StringComparison.Ordinal))
            {
                _outcome = "ProtocolMismatch";
                _faultKind = "ProtocolMismatch";
                _faultMessage = "技能端拒绝握手：协议主版本不匹配。";
                throw new ProtocolException(_faultMessage);
            }

            if (!string.Equals(handshakeEnvelope.Type, ProtocolConstants.MessageTypes.HelloAccepted, StringComparison.Ordinal))
            {
                _outcome = "ProtocolError";
                _faultKind = "UnexpectedHandshakeMessage";
                _faultMessage = $"期望 HelloAccepted，实际收到 {handshakeEnvelope.Type}。";
                throw new ProtocolException(_faultMessage);
            }

            var accepted = ProtocolMessages.ReadBody<HelloAcceptedBody>(handshakeEnvelope);
            if (accepted.ProtocolMajor != ProtocolConstants.CurrentProtocolMajor)
            {
                _outcome = "ProtocolMismatch";
                _faultKind = "ProtocolMismatch";
                _faultMessage = $"技能端支持的协议主版本 ({accepted.ProtocolMajor}) 与宿主当前主版本 ({ProtocolConstants.CurrentProtocolMajor}) 不匹配。";
                throw new ProtocolException(_faultMessage);
            }
        }
        catch (OperationCanceledException) when (handshakeCts.IsCancellationRequested && !ExecutionToken.IsCancellationRequested)
        {
            _handshakeTimedOut = true;
            throw;
        }
    }

    /// <summary>
    /// 在独立有限预算内下发初始 TaskInput，并注入 W3C 追踪上下文。
    /// </summary>
    /// <returns>下发输入的异步完成任务。</returns>
    private async Task SendTaskInputAsync()
    {
        using var taskInputCts = new CancellationTokenSource(_options.HandshakeTimeout, _options.TimeProvider);
        using var linkedInputCts = CancellationTokenSource.CreateLinkedTokenSource(taskInputCts.Token, ExecutionToken);

        try
        {
            await _connection!.SendAsync(
                ProtocolMessages.CreateTaskInput(
                    _invocation.TaskId,
                    _tempDirectory!.Path,
                    _invocation.InputTypeName,
                    _inputPayload,
                    _hostActivity!.Id,
                    _hostActivity.TraceStateString),
                linkedInputCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (taskInputCts.IsCancellationRequested && !ExecutionToken.IsCancellationRequested)
        {
            _outcome = "TaskInputSendTimeout";
            _faultKind = "TaskInputSendTimeout";
            _faultMessage = $"下发初始 TaskInput 超时（超过 {_options.HandshakeTimeout.TotalSeconds:F1} 秒），技能端未及时接收。";
            throw new TimeoutException(_faultMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _outcome = "TaskInputSendFault";
            _faultKind = "TaskInputSendFault";
            _faultMessage = $"下发初始 TaskInput 失败: {ex.Message}";
            throw;
        }
    }

    /// <summary>
    /// 移交执行协调器调度消息循环，接收前台执行结果，并在非正常终态时发送协同取消。
    /// </summary>
    /// <returns>消息循环前台部分的异步完成任务。</returns>
    private async Task RunCoordinatorAsync()
    {
        _coordinator = new HostExecutionCoordinator(
            _connection!,
            Registry,
            RootRecord,
            _childCalls!,
            Governance,
            _options,
            _invocation,
            Dispatcher,
            _cancellationToken,
            _executionCts!,
            ExecutionToken);

        _execResult = await _coordinator.ExecuteAsync().ConfigureAwait(false);
        _outcome = _execResult.Outcome;
        _outputTypeName = _execResult.OutputTypeName;
        _outputJson = _execResult.OutputJson;
        _streamItemCount = _execResult.StreamItemCount;
        _faultKind = _execResult.FaultKind;
        _faultMessage = _execResult.FaultMessage;
        _observedMessages = _execResult.ObservedMessages;
        _observedTruncated = _execResult.ObservedTruncated;

        if (_outcome is not ("SubmitResult" or "ResultClose") && _connection is { IsClosed: false })
        {
            await SendCancelOnStopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 向技能进程发送一次限时的协同取消通知；发送失败只保留诊断，不影响宿主收尾。
    /// </summary>
    /// <returns>发送取消的异步完成任务。</returns>
    private async Task SendCancelOnStopAsync()
    {
        if (_connection is null || _connection.IsClosed)
        {
            return;
        }

        try
        {
            using var cancelSendCts = new CancellationTokenSource(TimeSpan.FromSeconds(1), _options.TimeProvider);
            await _connection.SendAsync(ProtocolMessages.CreateCancel(), cancelSendCts.Token).ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
