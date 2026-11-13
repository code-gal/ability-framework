using System.Text.Json;
using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// 宿主端执行协调器（终态裁决、授权核验与辅助逻辑分部）。
/// </summary>
internal sealed partial class HostExecutionCoordinator
{
    /// <summary>
    /// 触发协调器受管故障断连流程。
    /// 记录故障分类，取消协调器级会话令牌，并启动受管任务主动异步关闭底层连接与等待断开。
    /// </summary>
    /// <param name="kind">故障分类原因。</param>
    /// <param name="error">导致断连的真实底层异常。</param>
    internal void TriggerFaultDisconnect(string kind, Exception error)
    {
        lock (_disconnectLock)
        {
            _faultDisconnectKind ??= kind;
            _faultDisconnectError ??= error;

            if (_faultCloseTask is not null)
            {
                return;
            }

            try
            {
                _disconnectCts.Cancel();
            }
            catch (Exception ex)
            {
                callbackDispatcher.RecordIssue("FaultDisconnect", ex.GetType().Name, DescribeSafeError(ex));
            }

            _faultCloseTask = Task.Run(async () =>
            {
                try
                {
                    await connection.CloseAsync(IpcTerminationReason.Disconnected, TimeSpan.Zero).ConfigureAwait(false);
                    await connection.WaitClosedAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    callbackDispatcher.DispatchDiagnostic($"受管断连收尾异常: {DescribeSafeError(ex)}");
                    callbackDispatcher.RecordIssue("FaultDisconnect", ex.GetType().Name, DescribeSafeError(ex));
                }
            });
        }
    }

    /// <summary>
    /// 统一裁决取消分类，精确区分操作未响应、调用方取消、执行超时与消费端故障，彻底消除返回 Unknown。
    /// </summary>
    /// <param name="currentFaultKind">当前已捕获的故障种类。</param>
    /// <param name="currentFaultMessage">当前已捕获的故障描述。</param>
    /// <param name="consumerTask">流元素消费任务（可选）。</param>
    /// <returns>裁决后的终态、故障种类与故障描述三元组。</returns>
    private (string Outcome, string FaultKind, string FaultMessage) ResolveCancellationOutcome(
        string? currentFaultKind,
        string? currentFaultMessage,
        Task? consumerTask)
    {
        if (string.Equals(currentFaultKind, "OperationUnresponsive", StringComparison.Ordinal))
        {
            return ("Fault", "OperationUnresponsive", currentFaultMessage ?? "受管操作未响应。");
        }

        if (callerToken.IsCancellationRequested)
        {
            return ("Cancelled", "OperationCanceled", "操作已被调用方取消。");
        }

        if (executionCts.IsCancellationRequested)
        {
            return ("ExecutionTimeout", "ExecutionTimeout", $"任务执行超时（{options.ExecutionTimeout?.TotalSeconds:F1} 秒）。");
        }

        if (consumerTask is not null && (consumerTask.IsFaulted || consumerTask.IsCanceled))
        {
            var isCanceled = consumerTask.IsCanceled;
            var kind = isCanceled ? "ResultItemCallbackCanceled" : "ResultItemCallbackFault";
            var msg = isCanceled
                ? "流元素消费回调已取消。"
                : $"流元素消费回调执行失败: {consumerTask.Exception?.InnerException?.GetType().Name}。";
            return ("ConsumerFault", kind, msg);
        }

        return ("Cancelled", currentFaultKind ?? "OperationCanceled", currentFaultMessage ?? "操作已取消。");
    }

    /// <summary>
    /// 校验消息发送方是否为受管操作的所属调用者（仅允许直接归属者操作）。
    /// </summary>
    /// <param name="messageOriginKey">消息来源调用键。</param>
    /// <param name="opOwnerKey">操作所属调用键。</param>
    /// <returns>若具有合法权限返回 true，否则返回 false。</returns>
    private static bool IsOriginAuthorizedForOperation(string messageOriginKey, string? opOwnerKey) =>
        !string.IsNullOrWhiteSpace(opOwnerKey) && string.Equals(messageOriginKey, opOwnerKey, StringComparison.Ordinal);

    /// <summary>
    /// 记录已观测消息类型。
    /// </summary>
    /// <param name="observedMessages">已观测消息列表。</param>
    /// <param name="messageType">消息类型标识。</param>
    /// <param name="truncated">截断标志位引用。</param>
    private static void RecordObservedMessage(List<string> observedMessages, string messageType, ref bool truncated)
    {
        if (observedMessages.Count < MaxObservedMessages)
        {
            observedMessages.Add(messageType);
        }
        else
        {
            truncated = true;
        }
    }

    /// <summary>
    /// 获取对外部观察安全的异常简要描述。
    /// </summary>
    /// <param name="ex">异常对象。</param>
    /// <returns>安全异常描述文本或类型名。</returns>
    private static string DescribeSafeError(Exception ex) =>
        ex is ISafeFaultDescription safe && !string.IsNullOrWhiteSpace(safe.SafeDescription)
            ? safe.SafeDescription
            : ex.GetType().Name;

    /// <summary>后台任务保留控制名额（16）。</summary>
    private const int ReservedControlSlots = 16;
    /// <summary>普通启动任务并发名额上限（48）。</summary>
    private const int MaxStartSlots = MaxBackgroundTasks - ReservedControlSlots;

    /// <summary>协调器内部启动的后台异步任务列表。</summary>
    private readonly List<Task> _backgroundTasks = [];
    /// <summary>后台任务列表同步锁。</summary>
    private readonly Lock _backgroundTasksLock = new();
    /// <summary>当前正在运行的启动任务计数。</summary>
    private int _activeStartTasks;
    /// <summary>按调用键记录的正在执行的停止任务字典，用于并发停止合并。</summary>
    private readonly Dictionary<string, Task<ChildStopResult>> _activeStopTasks = new(StringComparer.Ordinal);
    /// <summary>无响应子调用停止排队暂存容量上限（32）。</summary>
    private const int MaxPendingUnresponsiveStops = 32;
    /// <summary>无响应子调用紧急停止专用通道容量上限（16）。</summary>
    private const int MaxEmergencyStopTasks = 16;
    /// <summary>因容量满载而排队暂存的无响应子调用停止字典，按调用键合并去重。</summary>
    private readonly Dictionary<string, IReadOnlyList<InvocationRecord>> _pendingUnresponsiveStops = new(StringComparer.Ordinal);
    /// <summary>受管紧急停止异步任务追踪集合，持有所有权并在收尾时等待排空。</summary>
    private readonly HashSet<Task> _emergencyStopTasks = [];
    /// <summary>是否已关闭新后台任务的接纳。</summary>
    private bool _acceptingNewBackgroundTasks = true;
    /// <summary>后台任务池是否已彻底关闭。</summary>
    private bool _backgroundTasksClosed;

    /// <summary>
    /// 尝试登记并启动普通子启动任务（ChildStart）。
    /// 受普通启动槽位上限（48）与总上限（64）控制，为控制任务保留名额。
    /// </summary>
    /// <param name="taskFactory">产生后台任务的委托。</param>
    /// <returns>若成功接纳启动并登记返回 true；已关闭或满载返回 false。</returns>
    private bool TryStartLaunchTask(Func<Task> taskFactory)
    {
        lock (_backgroundTasksLock)
        {
            if (!_acceptingNewBackgroundTasks || _backgroundTasksClosed || _activeStartTasks >= MaxStartSlots || _backgroundTasks.Count >= MaxBackgroundTasks)
            {
                return false;
            }

            _activeStartTasks++;
            Task task = null!;
            task = Task.Run(async () =>
            {
                try
                {
                    await taskFactory().ConfigureAwait(false);
                }
                finally
                {
                    lock (_backgroundTasksLock)
                    {
                        _activeStartTasks--;
                        _backgroundTasks.Remove(task);
                        CheckPendingUnresponsiveStopsLocked();
                    }
                }
            });

            _backgroundTasks.Add(task);
            return true;
        }
    }

    /// <summary>
    /// 尝试登记并启动控制工作任务（治理收拢等）。
    /// 只要总任务数未达上限（64）且未关闭即可调度，享有预留槽位保障。
    /// </summary>
    /// <param name="taskFactory">产生后台任务的委托。</param>
    /// <returns>若成功接纳启动并登记返回 true；已关闭或满载返回 false。</returns>
    private bool TryStartControlTask(Func<Task> taskFactory)
    {
        lock (_backgroundTasksLock)
        {
            if (!_acceptingNewBackgroundTasks || _backgroundTasksClosed || _backgroundTasks.Count >= MaxBackgroundTasks)
            {
                return false;
            }

            Task task = null!;
            task = Task.Run(async () =>
            {
                try
                {
                    await taskFactory().ConfigureAwait(false);
                }
                finally
                {
                    lock (_backgroundTasksLock)
                    {
                        _backgroundTasks.Remove(task);
                        CheckPendingUnresponsiveStopsLocked();
                    }
                }
            });

            _backgroundTasks.Add(task);
            return true;
        }
    }

    /// <summary>
    /// 登记并调度针对特定调用树的 ChildStop 停止与租约回收任务。
    /// 若同一调用树已有停止任务正在运行，则合并等待已有任务并各自用独立请求号包装真实回收结果答复，防止重复启动后台工作。
    /// 完整生命周期（停止、撤销租约、结果发布与答复交付）纳入最外层 try/finally 块，答复写出完成前绝不提前归还容量。
    /// 共享完成源异常始终挂接观察延续；满载时拒绝调度并向发起方回发 ServerBusy 明确终结等待。
    /// </summary>
    /// <param name="requestId">当前 ChildStop 请求号。</param>
    /// <param name="invocationKey">目标子树根调用键。</param>
    /// <param name="subtree">已标记停止的子树权威记录集合。</param>
    /// <param name="responseWrapper">可选的信封包装委托。</param>
    /// <param name="connection">IPC 双工连接。</param>
    /// <param name="sessionToken">会话级取消令牌。</param>
    /// <returns>若成功接纳调度返回 true；满载或已关闭返回 false。</returns>
    private bool TryScheduleChildStop(
        long requestId,
        string invocationKey,
        IReadOnlyList<InvocationRecord> subtree,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper,
        IpcConnection connection,
        CancellationToken sessionToken)
    {
        lock (_backgroundTasksLock)
        {
            if (!_acceptingNewBackgroundTasks || _backgroundTasksClosed || _backgroundTasks.Count >= MaxBackgroundTasks)
            {
                return false;
            }

            if (_activeStopTasks.TryGetValue(invocationKey, out var existingStopTask))
            {
                // 合并请求的答复等待者同样计入容量并在收尾中完整追踪
                ScheduleMergedStopWaiter(requestId, invocationKey, existingStopTask, responseWrapper, connection, sessionToken);
                return true;
            }

            ScheduleNewStopTask(requestId, invocationKey, subtree, responseWrapper, connection, sessionToken);
            return true;
        }
    }

    /// <summary>
    /// 登记合并到同一调用树的停止等待者：复用共享停止任务，并用本请求编号包装真实结果答复。
    /// </summary>
    /// <param name="requestId">本次 ChildStop 请求编号。</param>
    /// <param name="invocationKey">目标子树根调用键。</param>
    /// <param name="existingStopTask">已在执行的共享停止任务。</param>
    /// <param name="responseWrapper">可选的信封包装委托。</param>
    /// <param name="connection">IPC 双工连接。</param>
    /// <param name="sessionToken">会话级取消令牌。</param>
    private void ScheduleMergedStopWaiter(
        long requestId,
        string invocationKey,
        Task<ChildStopResult> existingStopTask,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper,
        IpcConnection connection,
        CancellationToken sessionToken)
    {
        Task followUp = null!;
        followUp = Task.Run(async () =>
        {
            try
            {
                ChildStopResult result;
                try
                {
                    result = await existingStopTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    callbackDispatcher.DispatchDiagnostic($"合并等待 ChildStop 共享任务异常: {DescribeSafeError(ex)}");
                    await SendChildStopFailureReplyAsync(requestId, responseWrapper, connection, sessionToken, ex, "合并停止失败答复发送异常").ConfigureAwait(false);
                    return;
                }

                await TrySendStopReplyAsync(
                    BuildChildReapedReply(requestId, invocationKey, result),
                    responseWrapper,
                    connection,
                    sessionToken,
                    "合并停止成功答复发送异常").ConfigureAwait(false);
            }
            finally
            {
                lock (_backgroundTasksLock)
                {
                    _backgroundTasks.Remove(followUp);
                    CheckPendingUnresponsiveStopsLocked();
                }
            }
        });

        _backgroundTasks.Add(followUp);
    }

    /// <summary>
    /// 登记新的子树停止任务：先复用固定所有者的共享完成源，再执行停止、撤销租约并按真实结果答复。
    /// </summary>
    /// <param name="requestId">本次 ChildStop 请求编号。</param>
    /// <param name="invocationKey">目标子树根调用键。</param>
    /// <param name="subtree">已标记停止的子树权威记录集合。</param>
    /// <param name="responseWrapper">可选的信封包装委托。</param>
    /// <param name="connection">IPC 双工连接。</param>
    /// <param name="sessionToken">会话级取消令牌。</param>
    private void ScheduleNewStopTask(
        long requestId,
        string invocationKey,
        IReadOnlyList<InvocationRecord> subtree,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper,
        IpcConnection connection,
        CancellationToken sessionToken)
    {
        var stopTcs = new TaskCompletionSource<ChildStopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 确保即便无合并等待者，共享完成源的异常也始终被观察
        _ = stopTcs.Task.ContinueWith(
            t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        _activeStopTasks[invocationKey] = stopTcs.Task;

        Task task = null!;
        task = Task.Run(async () =>
        {
            try
            {
                ChildStopResult result;
                try
                {
                    var (stopResult, revokedLeases) = await childCalls.StopAsync(invocationKey, subtree).ConfigureAwait(false);
                    governance.RevokeLeases(revokedLeases);
                    result = stopResult;
                    stopTcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    stopTcs.TrySetException(ex);
                    callbackDispatcher.DispatchDiagnostic($"执行 ChildStop 异常: {DescribeSafeError(ex)}");
                    await SendChildStopFailureReplyAsync(requestId, responseWrapper, connection, sessionToken, ex, "ChildStop 失败答复发送异常").ConfigureAwait(false);
                    return;
                }
                finally
                {
                    // 停止操作本身完成后立即从活跃字典中移除，使得后续新请求可以启动新一轮停止
                    lock (_backgroundTasksLock)
                    {
                        _activeStopTasks.Remove(invocationKey);
                    }
                }

                await TrySendStopReplyAsync(
                    BuildChildReapedReply(requestId, invocationKey, result),
                    responseWrapper,
                    connection,
                    sessionToken,
                    "ChildStop 成功答复发送异常").ConfigureAwait(false);
            }
            finally
            {
                // 答复写出完全结束后才从后台任务中移除并归还容量，确保收尾等待看到真实在途任务
                lock (_backgroundTasksLock)
                {
                    _backgroundTasks.Remove(task);
                    CheckPendingUnresponsiveStopsLocked();
                }
            }
        });

        _backgroundTasks.Add(task);
    }

    /// <summary>
    /// 依据共享停止任务返回的真实结果构造 ChildReaped 答复，不伪造回收状态。
    /// </summary>
    /// <param name="requestId">本次 ChildStop 请求编号。</param>
    /// <param name="invocationKey">目标子树根调用键。</param>
    /// <param name="result">共享停止任务的真实结果。</param>
    /// <returns>待交付的 ChildReaped 答复信封。</returns>
    private static IpcEnvelope BuildChildReapedReply(long requestId, string invocationKey, ChildStopResult result)
    {
        return ProtocolMessages.CreateChildReaped(
            requestId,
            invocationKey,
            result.ExitCode,
            result.CleanedLeaseCount,
            result.Outcome,
            result.ExitedNaturally);
    }

    /// <summary>
    /// 写出 ChildStop 答复并隔离发送异常；答复写出失败只记录诊断，不影响共享结果所有者。
    /// </summary>
    /// <param name="reply">待交付的答复信封。</param>
    /// <param name="responseWrapper">可选的信封包装委托。</param>
    /// <param name="connection">IPC 双工连接。</param>
    /// <param name="sessionToken">会话级取消令牌。</param>
    /// <param name="diagnosticLabel">发送失败时的诊断前缀。</param>
    /// <returns>写出答复的异步完成任务。</returns>
    private async Task TrySendStopReplyAsync(
        IpcEnvelope reply,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper,
        IpcConnection connection,
        CancellationToken sessionToken,
        string diagnosticLabel)
    {
        try
        {
            await SendReplyAsync(reply, responseWrapper, connection, sessionToken).ConfigureAwait(false);
        }
        catch (Exception sendEx)
        {
            callbackDispatcher.DispatchDiagnostic($"{diagnosticLabel}: {DescribeSafeError(sendEx)}");
        }
    }

    /// <summary>
    /// 以带内层原始编号的小型故障答复终结 ChildStop 等待。
    /// </summary>
    /// <param name="requestId">本次 ChildStop 请求编号。</param>
    /// <param name="responseWrapper">可选的信封包装委托。</param>
    /// <param name="connection">IPC 双工连接。</param>
    /// <param name="sessionToken">会话级取消令牌。</param>
    /// <param name="error">导致停止失败的原异常。</param>
    /// <param name="diagnosticLabel">发送失败时的诊断前缀。</param>
    /// <returns>写出故障答复的异步完成任务。</returns>
    private async Task SendChildStopFailureReplyAsync(
        long requestId,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper,
        IpcConnection connection,
        CancellationToken sessionToken,
        Exception error,
        string diagnosticLabel)
    {
        var fault = ProtocolMessages.CreateRequestFault(requestId, "ChildStopFailed", DescribeSafeError(error));
        await TrySendStopReplyAsync(fault, responseWrapper, connection, sessionToken, diagnosticLabel).ConfigureAwait(false);
    }

    /// <summary>
    /// 在受控时限内写出答复信封，严格区分本地编码包装拒绝与底层传输失败。
    /// 本地包装或编码超限拒绝尝试构造并发送保持内外层协议结构的小型故障帧；
    /// 若无法交付小型故障或底层传输失败/超时，触发受管断连流程并重抛异常，终结等待。
    /// </summary>
    /// <param name="envelope">待发送信封。</param>
    /// <param name="responseWrapper">可选的信封包装委托。</param>
    /// <param name="connection">IPC 双工连接。</param>
    /// <param name="sessionToken">会话级取消令牌。</param>
    private async Task SendReplyAsync(
        IpcEnvelope envelope,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper,
        IpcConnection connection,
        CancellationToken sessionToken)
    {
        IpcEnvelope wrapped;
        try
        {
            wrapped = responseWrapper is not null ? responseWrapper(envelope) : envelope;
        }
        catch (Exception encodeEx)
        {
            callbackDispatcher.DispatchDiagnostic($"答复信封编码包装拒绝: {DescribeSafeError(encodeEx)}");
            if (await TrySendSmallFaultReplyAsync(envelope.Id, encodeEx, responseWrapper, connection, sessionToken).ConfigureAwait(false))
            {
                return;
            }

            TriggerFaultDisconnect("ReplyPackagingFault", encodeEx);
            throw;
        }

        try
        {
            using var sendCts = new CancellationTokenSource(DefaultSendTimeout, options.TimeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(sendCts.Token, sessionToken);
            await connection.SendAsync(wrapped, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            callbackDispatcher.DispatchDiagnostic($"回发答复信封传输失败: {DescribeSafeError(ex)}");

            // 若属于本地编码/超限且并非小型故障帧本身，尝试降级发送保持协议结构的小型故障帧
            if (!connection.IsClosed
                && !sessionToken.IsCancellationRequested
                && !string.Equals(envelope.Type, ProtocolConstants.MessageTypes.RequestFault, StringComparison.Ordinal)
                && ex is PayloadTooLargeException or JsonException or ArgumentException or NotSupportedException)
            {
                if (await TrySendSmallFaultReplyAsync(envelope.Id, ex, responseWrapper, connection, sessionToken).ConfigureAwait(false))
                {
                    return;
                }
            }

            var kind = ex switch
            {
                OperationCanceledException when !sessionToken.IsCancellationRequested => "ReplySendTimeout",
                PayloadTooLargeException or JsonException or ArgumentException or NotSupportedException => "ReplyEncodingFault",
                _ => "ReplyTransportFault"
            };
            TriggerFaultDisconnect(kind, ex);
            throw;
        }
    }

    /// <summary>
    /// 尝试构造并回发具有正确内外层协议结构的小型故障答复信封。
    /// </summary>
    /// <param name="requestId">待答复请求编号。</param>
    /// <param name="ex">导致降级的本地异常。</param>
    /// <param name="responseWrapper">可选的外层信封包装委托。</param>
    /// <param name="connection">IPC 双工连接。</param>
    /// <param name="sessionToken">会话级取消令牌。</param>
    /// <returns>若成功写出小型故障信封返回 true；否则返回 false。</returns>
    private async Task<bool> TrySendSmallFaultReplyAsync(
        long? requestId,
        Exception ex,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper,
        IpcConnection connection,
        CancellationToken sessionToken)
    {
        if (connection.IsClosed || sessionToken.IsCancellationRequested || requestId is not long reqId)
        {
            return false;
        }

        try
        {
            var faultKind = ex switch
            {
                PayloadTooLargeException => "ResponsePayloadTooLarge",
                JsonException => "PayloadEncodingError",
                ArgumentException => "InvalidResponsePayload",
                NotSupportedException => "UnsupportedPayloadFormat",
                _ => "ResponsePackagingError"
            };

            var faultMsg = ex switch
            {
                PayloadTooLargeException => "答复载荷超出协议允许的最大大小。",
                _ => "答复载荷本地包装或编码失败。"
            };

            var smallFault = ProtocolMessages.CreateRequestFault(reqId, faultKind, faultMsg);
            var wrappedSmall = responseWrapper is not null ? responseWrapper(smallFault) : smallFault;

            using var sendCts = new CancellationTokenSource(DefaultSendTimeout, options.TimeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(sendCts.Token, sessionToken);
            await connection.SendAsync(wrappedSmall, linked.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 登记并调度针对子操作无响应调用的停止与进程树回收。
    /// 若已有停止任务正在运行或已在暂存队列中则合并去重；
    /// 若后台任务池满载则有界排队暂存（上限 32），在后台任务结束时自动取出调度；
    /// 排队池亦满载时启用受限紧急停止通道（上限 16）直接执行强杀回收；
    /// 极端饱和时触发受管断连促使宿主整树回收，严禁仅写日志丢弃工作。
    /// </summary>
    /// <param name="invocationKey">目标子树根调用键。</param>
    /// <param name="subtree">已标记停止的子树权威记录集合。</param>
    private void ScheduleUnresponsiveStop(string invocationKey, IReadOnlyList<InvocationRecord> subtree)
    {
        lock (_backgroundTasksLock)
        {
            if (!_acceptingNewBackgroundTasks || _backgroundTasksClosed)
            {
                return;
            }

            if (_activeStopTasks.ContainsKey(invocationKey))
            {
                return;
            }

            if (_pendingUnresponsiveStops.ContainsKey(invocationKey))
            {
                return;
            }

            if (_backgroundTasks.Count < MaxBackgroundTasks)
            {
                StartUnresponsiveStopTaskLocked(invocationKey, subtree);
            }
            else if (_pendingUnresponsiveStops.Count < MaxPendingUnresponsiveStops)
            {
                _pendingUnresponsiveStops[invocationKey] = subtree;
                callbackDispatcher.DispatchDiagnostic($"后台任务池满载，排队暂存无响应子树停止：{invocationKey}。");
            }
            else if (_emergencyStopTasks.Count < MaxEmergencyStopTasks)
            {
                StartEmergencyStopTaskLocked(invocationKey, subtree);
                callbackDispatcher.DispatchDiagnostic($"停止排队池已满，启用受限紧急通道异步回收无响应子树：{invocationKey}。");
            }
            else
            {
                callbackDispatcher.DispatchDiagnostic($"停止容量彻底耗尽（总项数达到 112），触发受管断连收尾整树回收：{invocationKey}。");
                TriggerFaultDisconnect("UnresponsiveStopCapacityExhausted", new InvalidOperationException($"子调用停止容量耗尽，触发宿主整树回收: {invocationKey}"));
            }
        }
    }

    /// <summary>
    /// 在锁内启动针对无响应子树的实际停止工作任务。
    /// </summary>
    /// <param name="invocationKey">目标子树根调用键。</param>
    /// <param name="subtree">已标记停止的子树权威记录集合。</param>
    private void StartUnresponsiveStopTaskLocked(string invocationKey, IReadOnlyList<InvocationRecord> subtree)
    {
        var stopTcs = new TaskCompletionSource<ChildStopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = stopTcs.Task.ContinueWith(
            t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        _activeStopTasks[invocationKey] = stopTcs.Task;

        Task task = null!;
        task = Task.Run(async () =>
        {
            try
            {
                var (result, revokedLeases) = await childCalls.StopAsync(invocationKey, subtree).ConfigureAwait(false);
                governance.RevokeLeases(revokedLeases);
                stopTcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                stopTcs.TrySetException(ex);
                callbackDispatcher.DispatchDiagnostic($"子操作无响应联动停止子树异常: {DescribeSafeError(ex)}");
            }
            finally
            {
                lock (_backgroundTasksLock)
                {
                    _activeStopTasks.Remove(invocationKey);
                    _backgroundTasks.Remove(task);
                    CheckPendingUnresponsiveStopsLocked();
                }
            }
        });

        _backgroundTasks.Add(task);
    }

    /// <summary>
    /// 在锁内启动受限紧急停止任务，直接执行物理强杀与租约注销，并纳入受管所有权追踪与收尾汇聚。
    /// </summary>
    /// <param name="invocationKey">目标子树根调用键。</param>
    /// <param name="subtree">已标记停止的子树权威记录集合。</param>
    private void StartEmergencyStopTaskLocked(string invocationKey, IReadOnlyList<InvocationRecord> subtree)
    {
        var stopTcs = new TaskCompletionSource<ChildStopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = stopTcs.Task.ContinueWith(
            t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        _activeStopTasks[invocationKey] = stopTcs.Task;

        Task task = null!;
        task = Task.Run(async () =>
        {
            try
            {
                var (result, revokedLeases) = await childCalls.StopAsync(invocationKey, subtree).ConfigureAwait(false);
                governance.RevokeLeases(revokedLeases);
                stopTcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                stopTcs.TrySetException(ex);
                callbackDispatcher.DispatchDiagnostic($"紧急停止无响应子树异常: {DescribeSafeError(ex)}");
            }
            finally
            {
                lock (_backgroundTasksLock)
                {
                    _activeStopTasks.Remove(invocationKey);
                    _emergencyStopTasks.Remove(task);
                }
            }
        });

        _emergencyStopTasks.Add(task);
    }

    /// <summary>
    /// 在锁内检查并启动暂存的无响应子树停止任务。
    /// </summary>
    private void CheckPendingUnresponsiveStopsLocked()
    {
        while (_pendingUnresponsiveStops.Count > 0 && _backgroundTasks.Count < MaxBackgroundTasks && !_backgroundTasksClosed)
        {
            var first = _pendingUnresponsiveStops.First();
            _pendingUnresponsiveStops.Remove(first.Key);
            if (_activeStopTasks.ContainsKey(first.Key))
            {
                continue;
            }

            StartUnresponsiveStopTaskLocked(first.Key, first.Value);
        }
    }

    /// <summary>
    /// 在收尾时关闭新任务登记并在有限时限内等待所有在途后台任务完全排空。
    /// 优先调度排队的停止请求，汇聚等待常规后台任务与受管紧急停止任务；未获调度项显式清空队列并移交宿主整树回收。
    /// </summary>
    private async Task DrainBackgroundTasksAsync()
    {
        List<Task> pending;
        lock (_backgroundTasksLock)
        {
            _acceptingNewBackgroundTasks = false;

            // 尽最大能力将暂存排队的无响应停止请求调度启动
            while (_pendingUnresponsiveStops.Count > 0 && _backgroundTasks.Count < MaxBackgroundTasks)
            {
                var first = _pendingUnresponsiveStops.First();
                _pendingUnresponsiveStops.Remove(first.Key);
                if (!_activeStopTasks.ContainsKey(first.Key))
                {
                    StartUnresponsiveStopTaskLocked(first.Key, first.Value);
                }
            }

            // 若容量不足仍有未启动项，显式清空队列并记录诊断，移交宿主 StopAllAsync 统一整树回收
            if (_pendingUnresponsiveStops.Count > 0)
            {
                callbackDispatcher.DispatchDiagnostic($"收尾时存在 {_pendingUnresponsiveStops.Count} 个排队停止任务未获调度名额，移交宿主 StopAllAsync 统一回收。");
                _pendingUnresponsiveStops.Clear();
            }

            pending = [.. _backgroundTasks, .. _emergencyStopTasks];
        }

        if (pending.Count > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(3), options.TimeProvider).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                callbackDispatcher.DispatchDiagnostic($"排空后台任务超时或异常: {DescribeSafeError(ex)}");
            }
        }

        lock (_backgroundTasksLock)
        {
            _backgroundTasksClosed = true;
            _pendingUnresponsiveStops.Clear();
        }
    }

    /// <summary>
    /// 承载协调器内部所有工作完全物理退出信号的异步源。
    /// </summary>
    private readonly TaskCompletionSource _physicalExitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 登记流消费、策略更新及相关延续/取消任务，在所有后台资源物理完全退出后触发通知源。
    /// </summary>
    /// <param name="policyTask">策略更新任务。</param>
    /// <param name="consumerTask">流元素消费任务。</param>
    /// <param name="consumerPhysicalExitTask">消费端完全物理退出任务。</param>
    /// <param name="cancelConsumerTask">可选的取消消费任务。</param>
    /// <param name="consumerDisposeTask">可选的消费者取消源释放延续任务。</param>
    /// <param name="cancelPolicyTask">可选的取消策略更新任务。</param>
    /// <param name="policyDisposeTask">可选的策略取消源释放延续任务。</param>
    private void TrackPhysicalExit(
        Task? policyTask,
        Task? consumerTask,
        Task? consumerPhysicalExitTask = null,
        Task? cancelConsumerTask = null,
        Task? consumerDisposeTask = null,
        Task? cancelPolicyTask = null,
        Task? policyDisposeTask = null)
    {
        Task? faultClose;
        lock (_disconnectLock)
        {
            faultClose = _faultCloseTask;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (faultClose is not null)
                {
                    try { await faultClose.WaitAsync(TimeSpan.FromSeconds(2), options.TimeProvider).ConfigureAwait(false); }
                    catch (Exception ex) { callbackDispatcher.RecordIssue("CoordinatorPhysicalExit", ex.GetType().Name, DescribeSafeError(ex)); }
                }

                if (policyTask is not null)
                {
                    try { await policyTask.ConfigureAwait(false); }
                    catch (Exception ex) { callbackDispatcher.RecordIssue("CoordinatorPhysicalExit", ex.GetType().Name, DescribeSafeError(ex)); }
                }

                if (cancelPolicyTask is not null)
                {
                    try { await cancelPolicyTask.ConfigureAwait(false); }
                    catch (Exception ex) { callbackDispatcher.RecordIssue("CoordinatorPhysicalExit", ex.GetType().Name, DescribeSafeError(ex)); }
                }

                if (policyDisposeTask is not null)
                {
                    try { await policyDisposeTask.ConfigureAwait(false); }
                    catch (Exception ex) { callbackDispatcher.RecordIssue("CoordinatorPhysicalExit", ex.GetType().Name, DescribeSafeError(ex)); }
                }

                if (consumerTask is not null)
                {
                    try { await consumerTask.ConfigureAwait(false); }
                    catch (Exception ex) { callbackDispatcher.RecordIssue("CoordinatorPhysicalExit", ex.GetType().Name, DescribeSafeError(ex)); }
                }

                if (cancelConsumerTask is not null)
                {
                    try { await cancelConsumerTask.ConfigureAwait(false); }
                    catch (Exception ex) { callbackDispatcher.RecordIssue("CoordinatorPhysicalExit", ex.GetType().Name, DescribeSafeError(ex)); }
                }

                if (consumerDisposeTask is not null)
                {
                    try { await consumerDisposeTask.ConfigureAwait(false); }
                    catch (Exception ex) { callbackDispatcher.RecordIssue("CoordinatorPhysicalExit", ex.GetType().Name, DescribeSafeError(ex)); }
                }

                if (consumerPhysicalExitTask is not null)
                {
                    try { await consumerPhysicalExitTask.ConfigureAwait(false); }
                    catch (Exception ex) { callbackDispatcher.RecordIssue("CoordinatorPhysicalExit", ex.GetType().Name, DescribeSafeError(ex)); }
                }

                Task[] remaining;
                lock (_backgroundTasksLock)
                {
                    remaining = [.. _backgroundTasks, .. _emergencyStopTasks];
                }
                if (remaining.Length > 0)
                {
                    try { await Task.WhenAll(remaining).ConfigureAwait(false); }
                    catch (Exception ex) { callbackDispatcher.RecordIssue("CoordinatorPhysicalExit", ex.GetType().Name, DescribeSafeError(ex)); }
                }
            }
            finally
            {
                _physicalExitTcs.TrySetResult();
            }
        });
    }

    /// <summary>
    /// 等待协调器内部所有工作循环、LateReply、子路由转发及受管后台任务完全物理退出。
    /// 供 <see cref="HostRunLifetime"/> 追踪使用。
    /// </summary>
    /// <returns>等待完全退出的异步任务。</returns>
    public Task WaitForPhysicalExitAsync() => _physicalExitTcs.Task;

    /// <summary>
    /// 获取协调器内部所有后台工作是否均已物理退出。
    /// </summary>
    public bool IsPhysicallyExited => _physicalExitTcs.Task.IsCompleted;

    /// <summary>
    /// 获取协调器前台有限排空是否已结束。
    /// </summary>
    public bool IsDrained
    {
        get
        {
            lock (_backgroundTasksLock)
            {
                return _backgroundTasksClosed && _backgroundTasks.Count == 0 && _emergencyStopTasks.Count == 0;
            }
        }
    }

    /// <summary>
    /// 向外层中继路由回发无内层答复的通知确认信封，闭环结束外层请求等待。
    /// </summary>
    /// <param name="route">外层中继路由链。</param>
    /// <param name="activeSessionToken">有效会话生命周期取消令牌。</param>
    private async Task AckChildNotificationAsync(ChildMessageRoute? route, CancellationToken activeSessionToken)
    {
        if (route is not null && route.OuterRequestId > 0)
        {
            IpcEnvelope ack;
            try
            {
                ack = ChildMessageRouter.WrapChildResponse(route, null);
            }
            catch (Exception encodeEx)
            {
                callbackDispatcher.DispatchDiagnostic($"通知确认信封包装拒绝: {DescribeSafeError(encodeEx)}");
                TriggerFaultDisconnect("NotificationAckPackagingFault", encodeEx);
                throw;
            }

            try
            {
                using var sendCts = new CancellationTokenSource(DefaultSendTimeout, options.TimeProvider);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(sendCts.Token, activeSessionToken);
                await connection.SendAsync(ack, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                callbackDispatcher.DispatchDiagnostic($"通知确认传输失败: {DescribeSafeError(ex)}");
                var kind = ex switch
                {
                    OperationCanceledException when !sessionToken.IsCancellationRequested => "NotificationAckTimeout",
                    PayloadTooLargeException or JsonException or ArgumentException or NotSupportedException => "NotificationAckEncodingFault",
                    _ => "NotificationAckTransportFault"
                };
                TriggerFaultDisconnect(kind, ex);
                throw;
            }
        }
    }
}
