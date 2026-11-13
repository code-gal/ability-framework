using System.Text.Json;
using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// 负责宿主端治理能力的状态汇总与业务协调（答复交付与连接管理分部）。
/// </summary>
internal sealed partial class HostGovernanceCoordinator
{
    /// <summary>
    /// 向外部安全分发一条诊断文本。
    /// 优先接入受管有界派发机制，隔离诊断失败与阻塞；若未配置外部回调，异步投递至受限后台线程，绝不阻塞当前处置逻辑与关闭连接。
    /// </summary>
    /// <param name="message">诊断消息内容。</param>
    private void ReportDiagnostic(string message)
    {
        try
        {
            if (_diagnosticCallback is not null)
            {
                _diagnosticCallback(message);
            }
            else
            {
                ThreadPool.QueueUserWorkItem(static state =>
                {
                    try
                    {
                        Console.Error.WriteLine((string)state!);
                    }
                    catch
                    {
                    }
                }, message);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 当当前任务已取得裁决所有权后，安全交付答复信封或故障信封。
    /// 将构造、格式化、包装、发送和降级完整纳入异常保护；若发生任何意外异常，可靠执行受管关闭以终结调用端挂起等待。
    /// </summary>
    /// <param name="connection">通信连接实例。</param>
    /// <param name="buildEnvelopeAsync">答复信封异步构建委托。</param>
    /// <param name="replyReqId">客户端原始请求编号。</param>
    /// <param name="requestType">治理请求类型名称。</param>
    /// <param name="responseWrapper">可选的外层信封包装器。</param>
    /// <param name="sessionToken">宿主会话取消令牌。</param>
    private async Task DeliverAdjudicatedReplySafelyAsync(
        IpcConnection connection,
        Func<Task<IpcEnvelope?>> buildEnvelopeAsync,
        long replyReqId,
        string requestType,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper,
        CancellationToken sessionToken)
    {
        try
        {
            IpcEnvelope? replyEnv;
            try
            {
                replyEnv = await buildEnvelopeAsync().ConfigureAwait(false);
            }
            catch (Exception buildEx)
            {
                RecordIssue("GovernanceReplyBuild", buildEx);
                ReportDiagnostic($"[HostGovernanceCoordinator] 治理请求 {requestType} 答复信封构造异常: {DescribeSafeError(buildEx)}");
                replyEnv = ProtocolMessages.CreateRequestFault(replyReqId, "DecisionFault", $"构建答复信封失败: {DescribeSafeError(buildEx)}");
            }

            if (replyEnv is null)
            {
                replyEnv = ProtocolMessages.CreateRequestFault(replyReqId, "DecisionFault", "宿主治理决策未生成有效答复信封。");
            }

            await DeliverReplyOrCloseAsync(connection, replyEnv, responseWrapper, sessionToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordIssue("GovernanceDeliveryFatal", ex);
            CloseConnectionSafely(connection);
            ReportDiagnostic($"[HostGovernanceCoordinator] 治理请求 {requestType} 交付严重异常: {DescribeSafeError(ex)}");
        }
    }

    /// <summary>
    /// 交付治理答复信封；首次发送增加独立有限预算（2 秒），覆盖等待出站名额与物理写出。
    /// 区分本地构造/编码失败与传输受阻：本地错误尝试发送小型故障帧，传输受阻或连接断开则立即终止底层连接，并持有观察关闭任务。
    /// </summary>
    /// <param name="connection">通信连接实例。</param>
    /// <param name="innerEnvelope">待交付的内层答复信封。</param>
    /// <param name="responseWrapper">可选的外层信封包装器（用于子调用消息回复转发）。</param>
    /// <param name="sessionToken">宿主会话取消令牌。</param>
    /// <returns>若答复成功交付返回 true；若传输受阻、超时或断连返回 false。</returns>
    private async Task<bool> DeliverReplyOrCloseAsync(
        IpcConnection connection,
        IpcEnvelope innerEnvelope,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper,
        CancellationToken sessionToken)
    {
        if (connection.IsClosed)
        {
            return false;
        }

        try
        {
            IpcEnvelope wrapped;
            try
            {
                wrapped = responseWrapper is not null ? responseWrapper(innerEnvelope) : innerEnvelope;
            }
            catch (Exception wrapEx)
            {
                ReportDiagnostic($"[HostGovernanceCoordinator] 治理信封 {innerEnvelope.Type} 本地包装失败: {wrapEx.GetType().Name}");
                return await TryDeliverSmallFaultOrCloseAsync(connection, innerEnvelope.Id, wrapEx, responseWrapper, sessionToken).ConfigureAwait(false);
            }

            // 首次发送施加独立有限预算（2 秒），覆盖等待出站名额与物理写出
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
            sendCts.CancelAfter(TimeSpan.FromSeconds(2));

            try
            {
                await connection.SendAsync(wrapped, sendCts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!sessionToken.IsCancellationRequested)
            {
                // 发送超时：出站队列满载或管道背压受阻，优先终止连接终结等待，诊断派发安全隔离
                CloseConnectionSafely(connection);
                ReportDiagnostic($"[HostGovernanceCoordinator] 治理信封 {wrapped.Type} 首次发送超时受阻，主动终止连接。");
                return false;
            }
            catch (Exception ex)
            {
                RecordIssue("GovernanceDelivery", ex);
                ReportDiagnostic($"[HostGovernanceCoordinator] 治理信封 {wrapped.Type} 交付失败: {ex.GetType().Name}");

                // 若并非小型故障帧本身，且属于本地超限、序列化或编码等本地构造错误，尝试包装并发送小型故障帧
                if (!connection.IsClosed
                    && !sessionToken.IsCancellationRequested
                    && !string.Equals(innerEnvelope.Type, ProtocolConstants.MessageTypes.RequestFault, StringComparison.Ordinal)
                    && ex is PayloadTooLargeException or JsonException or ArgumentException or NotSupportedException)
                {
                    return await TryDeliverSmallFaultOrCloseAsync(connection, innerEnvelope.Id, ex, responseWrapper, sessionToken).ConfigureAwait(false);
                }

                // 传输受阻、IpcException 或二次发送失败：立即终止底层连接并持有观察关闭任务
                CloseConnectionSafely(connection);
                return false;
            }
        }
        catch (Exception ex)
        {
            RecordIssue("GovernanceDeliveryUnhandled", ex);
            // 交付管道全局兜底：若发生未预料异常，立即受管关闭连接，绝不悬挂客户端等待
            CloseConnectionSafely(connection);
            ReportDiagnostic($"[HostGovernanceCoordinator] 治理信封交付未捕获异常: {DescribeSafeError(ex)}");
            return false;
        }
    }

    /// <summary>
    /// 尝试构造、包装并发送小型故障答复；若仍无法包装或发送失败，则安全关闭连接。
    /// 保持内层小型故障帧与外层 ChildMessageReply 的正确协议结构。
    /// </summary>
    /// <param name="connection">通信连接实例。</param>
    /// <param name="innerRequestId">客户端内层原始请求编号。</param>
    /// <param name="ex">导致降级的本地异常。</param>
    /// <param name="responseWrapper">可选的外层信封包装器。</param>
    /// <param name="sessionToken">宿主会话取消令牌。</param>
    /// <returns>若小型故障答复成功交付返回 true；否则返回 false。</returns>
    private async Task<bool> TryDeliverSmallFaultOrCloseAsync(
        IpcConnection connection,
        long? innerRequestId,
        Exception ex,
        Func<IpcEnvelope, IpcEnvelope>? responseWrapper,
        CancellationToken sessionToken)
    {
        if (connection.IsClosed || sessionToken.IsCancellationRequested || innerRequestId is not long reqId)
        {
            CloseConnectionSafely(connection);
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

            var faultMessage = ex switch
            {
                PayloadTooLargeException => "宿主治理答复信封尺寸超出协议上限。",
                _ => "宿主治理答复信封本地编码或构造失败。"
            };

            var smallFault = ProtocolMessages.CreateRequestFault(reqId, faultKind, faultMessage);
            var wrappedSmall = responseWrapper is not null ? responseWrapper(smallFault) : smallFault;

            using var smallFaultCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
            smallFaultCts.CancelAfter(TimeSpan.FromSeconds(1));
            await connection.SendAsync(wrappedSmall, smallFaultCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception smallEx)
        {
            // 小型故障亦发送失败或无法包装，继续向下终止连接
            CloseConnectionSafely(connection);
            ReportDiagnostic($"[HostGovernanceCoordinator] 小型故障答复交付失败: {DescribeSafeError(smallEx)}");
            return false;
        }
    }

    /// <summary>
    /// 安全关闭底层连接，持有并观察关闭任务。
    /// </summary>
    /// <param name="connection">通信连接实例。</param>
    private static void CloseConnectionSafely(IpcConnection connection)
    {
        try
        {
            if (connection.IsClosed)
            {
                return;
            }

            var closeTask = connection.CloseAsync(IpcTerminationReason.Disconnected);
            _ = closeTask.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 提取安全的脱敏异常描述。
    /// </summary>
    /// <param name="ex">异常对象。</param>
    /// <returns>脱敏后的安全错误文本。</returns>
    private static string DescribeSafeError(Exception ex)
    {
        if (ex is ISafeFaultDescription safe && !string.IsNullOrWhiteSpace(safe.SafeDescription))
        {
            return safe.SafeDescription;
        }

        return ex.GetType().Name;
    }

    /// <summary>
    /// 当进程退出时，按操作所属调用的真实退出事实分别收口尚未结束的受管操作。
    /// 所属调用未确认退出终态（Reaped）者保留真实未退出状态，不强制伪造退出。
    /// </summary>
    /// <param name="registry">调用权威登记表。</param>
    /// <param name="rootExitCode">根调用退出码。</param>
    /// <param name="rootExitedNaturally">根调用是否自然退出。</param>
    /// <param name="snapshotCallback">操作快照回调通知委托（可选）。</param>
    /// <param name="diagnosticCallback">诊断警告回调通知委托（可选）。</param>
    public void EndOperationsForProcessExit(
        InvocationRegistry registry,
        int? rootExitCode,
        bool? rootExitedNaturally,
        Action<OperationSnapshot>? snapshotCallback,
        Action<string>? diagnosticCallback = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        foreach (var (opKey, operation) in _operations)
        {
            var opOwner = registry.GetOperationOwner(opKey);
            int? exitCode;
            bool? naturally;

            if (opOwner is not null)
            {
                if (registry.TryGetHistorical(opOwner, out var ownerRecord) && ownerRecord is not null)
                {
                    if (ownerRecord.State != InvocationState.Reaped)
                    {
                        continue;
                    }

                    exitCode = ownerRecord.ExitCode;
                    naturally = ownerRecord.ExitedNaturally;
                }
                else
                {
                    // 操作关联的调用键在权威历史中不存在，明确保留未知退出事实，严禁调用 EndForProcessExit 伪造终态
                    diagnosticCallback?.Invoke($"[Warning] 受管操作 '{opKey}' 关联的调用 '{opOwner}' 在权威历史中不存在，保留未知退出事实。");
                    continue;
                }
            }
            else
            {
                // 缺失所有者，明确保留未知事实并记录诊断，严禁调用 EndForProcessExit 伪造终态
                diagnosticCallback?.Invoke($"[Warning] 受管操作 '{opKey}' 缺失所属调用所有者，保留未知退出事实。");
                continue;
            }

            if (operation.EndForProcessExit(exitCode, naturally))
            {
                try
                {
                    snapshotCallback?.Invoke(operation.Snapshot());
                }
                catch
                {
                }
            }
        }
    }
}
