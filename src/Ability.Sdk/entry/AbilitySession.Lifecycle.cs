using System.Collections.Concurrent;
using Ability.Core;
using Ability.Model;

namespace Ability.Sdk;

/// <summary>
/// 负责单个任务进程内 IPC 会话全生命周期协调（收尾与资源释放分部）。
/// </summary>
internal sealed partial class AbilitySession
{
    /// <summary>
    /// 当前在途的活动会话使用者计数（涵盖在途登记、操作释放与尝试物理退出任务）。
    /// 由 _operationsGate 锁保护。
    /// </summary>
    private int _activeSessionUsers;

    /// <summary>
    /// 当活动会话使用者计数归零时的通知完成源。
    /// 由 _operationsGate 锁保护。
    /// </summary>
    private TaskCompletionSource _zeroUsersTcs = CreateCompletedZeroUsersTcs();

    /// <summary>
    /// 创建初始已完成状态的零使用者通知源。
    /// </summary>
    private static TaskCompletionSource CreateCompletedZeroUsersTcs()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.TrySetResult();
        return tcs;
    }

    /// <summary>
    /// 在 _operationsGate 互斥锁保护下递增活动使用者计数，并根据需要重置零在途通知源。
    /// </summary>
    private void EnterSessionUserLocked()
    {
        _activeSessionUsers++;
        if (_zeroUsersTcs.Task.IsCompleted)
        {
            _zeroUsersTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>
    /// 减少一个活动会话使用者，当计数归零时触发完成源。
    /// </summary>
    private void LeaveSessionUser()
    {
        lock (_operationsGate)
        {
            if (--_activeSessionUsers <= 0)
            {
                _activeSessionUsers = 0;
                _zeroUsersTcs.TrySetResult();
            }
        }
    }

    /// <summary>
    /// 获取等待所有活动会话使用者完全归零的异步任务（状态驱动通知，杜绝 WhenAny 轮询忙循环）。
    /// </summary>
    private Task WaitForZeroSessionUsersAsync()
    {
        lock (_operationsGate)
        {
            return _activeSessionUsers == 0 ? Task.CompletedTask : _zeroUsersTcs.Task;
        }
    }

    /// <summary>
    /// 登记受管操作单次尝试的物理退出任务，由会话全局生命周期追踪（涵盖已注销的操作）。
    /// 会话关闭后拒绝登记新尝试，直接抛出 ObjectDisposedException 触发操作端回滚。
    /// </summary>
    /// <param name="exitTask">代表尝试物理完全退出的任务。</param>
    internal void TrackAttemptExitTask(Task exitTask)
    {
        lock (_operationsGate)
        {
            if (_operationsClosed || Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(AbilitySession), "会话已关闭，拒绝登记新尝试。");
            }

            EnterSessionUserLocked();
        }

        _ = exitTask.ContinueWith(static (t, state) =>
        {
            _ = t.Exception;
            var session = (AbilitySession)state!;
            session.LeaveSessionUser();
        }, this, TaskScheduler.Default);
    }

    /// <summary>
    /// 登记受管操作的释放任务（含常规收尾与迟到释放），由会话全局生命周期追踪并纳入收尾与取消源释放裁决。
    /// </summary>
    /// <param name="task">代表释放执行的任务。</param>
    internal void TrackOperationDisposalTask(Task task)
    {
        lock (_operationsGate)
        {
            EnterSessionUserLocked();
        }

        _ = task.ContinueWith(static (t, state) =>
        {
            _ = t.Exception;
            var session = (AbilitySession)state!;
            session.LeaveSessionUser();
        }, this, TaskScheduler.Default);
    }

    /// <summary>
    /// 登记会话异步工作任务（如枚举器推进退出或释放），由会话全局生命周期追踪并纳入收尾与取消源释放裁决。
    /// </summary>
    /// <param name="task">代表后台工作的任务。</param>
    internal void TrackSessionWork(Task task)
    {
        lock (_operationsGate)
        {
            EnterSessionUserLocked();
        }

        _ = task.ContinueWith(static (t, state) =>
        {
            _ = t.Exception;
            var session = (AbilitySession)state!;
            session.LeaveSessionUser();
        }, this, TaskScheduler.Default);
    }

    /// <summary>
    /// 业务取消任务互斥锁。
    /// </summary>
    private readonly object _cancellationGate = new();

    /// <summary>
    /// 统一持有的后台业务取消执行任务。
    /// </summary>
    private Task? _businessCancelTask;

    /// <summary>
    /// 触发业务取消并返回统一的后台取消任务。
    /// 统一入站泵 Cancel 指令、断连及会话收尾各路径的取消执行所有权，
    /// 采用后台异步隔离执行，杜绝外部注册的回调同步阻塞或抛错阻断通信分发与收尾推进。
    /// </summary>
    /// <returns>代表实际取消执行的后台任务。</returns>
    internal Task TriggerBusinessCancellation()
    {
        lock (_cancellationGate)
        {
            if (_businessCancelTask is not null)
            {
                return _businessCancelTask;
            }

            _businessCancelTask = Task.Run(() =>
            {
                try
                {
                    _businessCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                catch
                {
                    // 吸收外部注册回调可能抛出的任何未捕获异常
                }
            });

            _ = _businessCancelTask.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
            return _businessCancelTask;
        }
    }

    /// <summary>
    /// 会话收尾执行任务门控互斥锁。
    /// </summary>
    private readonly object _teardownGate = new();

    /// <summary>
    /// 会话统一持有的收尾执行任务，供正常退出、故障、取消和 DisposeAsync 共同使用，杜绝重复清理与所有权丢失。
    /// </summary>
    private Task<bool>? _teardownTask;

    /// <summary>
    /// 确保会话统一收尾任务已启动并返回其执行任务。
    /// </summary>
    /// <param name="externalBudgetToken">可选的外部共享收尾预算取消令牌（若未传入则创建独立 3 秒预算）。</param>
    /// <returns>收尾是否完全成功（所有操作正常收尾且所有租约释放成功排空）。</returns>
    internal Task<bool> EnsureTeardownAsync(CancellationToken externalBudgetToken = default)
    {
        lock (_teardownGate)
        {
            _teardownTask ??= PerformTeardownAsync(externalBudgetToken);
            return _teardownTask;
        }
    }

    /// <summary>
    /// 在发送终态消息或关闭连接前，统一清理在途业务工作：
    /// 1. 阻止新工作（关闭操作登记，取消在途登记请求，触发业务取消）；
    /// 2. 有限时限内收尾已登记的受管操作并等待在途尝试退出；
    /// 3. 确认在途业务停止后，释放剩余活动租约并在有限预算内排空队列。
    /// </summary>
    /// <param name="externalBudgetToken">外部传入的共享预算取消令牌。</param>
    private async Task<bool> PerformTeardownAsync(CancellationToken externalBudgetToken = default)
    {
        var success = true;

        // 1. 阻止新工作：触发业务取消，通知在途登记请求取消，原子置位操作关闭标记
        _ = TriggerBusinessCancellation();
        ManagedOperation[] ops;
        lock (_operationsGate)
        {
            _operationsClosed = true;
            try { _registrationClosingCts.Cancel(); } catch { }
            ops = _operations.Values.ToArray();
            _operations.Clear();

            foreach (var pending in _pendingRegistrations.Values)
            {
                pending.TrySetException(new ObjectDisposedException(nameof(AbilitySession), "会话已进入收尾阶段。"));
            }
            _pendingRegistrations.Clear();
        }

        // 统一 3 秒收尾总预算，若已提供外部共享预算则直接使用该预算（不重新开始计时），否则创建独立 3 秒预算
        CancellationTokenSource? localTimeoutCts = null;
        var teardownToken = externalBudgetToken;
        if (teardownToken == default)
        {
            localTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            teardownToken = localTimeoutCts.Token;
        }

        try
        {
            // 2. 收尾受管操作及在途登记、操作释放和尝试退出任务（在共享 3 秒总预算内等待完全归零）
        if (ops.Length > 0)
        {
            foreach (var op in ops)
            {
                var disposeTask = Task.Run(async () => await op.DisposeAsync().ConfigureAwait(false));
                TrackOperationDisposalTask(disposeTask);
            }
        }

        var zeroUsersTask = WaitForZeroSessionUsersAsync();
        try
        {
            await zeroUsersTask.WaitAsync(teardownToken).ConfigureAwait(false);
        }
        catch
        {
            success = false;
        }

        // 3. 先停止子调用路由并等待其排空，使用统一收尾预算；
        // 若子路由未能在此预算内完全停止，如实标记收尾失败
        try
        {
            await _childRouter.DisposeAsync(teardownToken).ConfigureAwait(false);
            if (_childRouter.HasFailedStops)
            {
                success = false;
            }
        }
        catch
        {
            success = false;
        }

        // 4. 只有当受管操作与子调用在预算内完全停止确认成功时，才释放剩余活动租约并在共享预算内排空；
        // 若在途业务未能在此预算内停下，严禁提前释放活动租约以防破坏进行中的受管操作与子调用，保留由宿主回收
        if (success)
        {
            if (_connection is { IsClosed: false })
            {
                foreach (var leaseId in _leaseManager.ActiveLeaseIds)
                {
                    _leaseManager.EnqueueRelease(leaseId, _connection);
                }
            }

            try
            {
                var flushed = await _leaseManager.FlushReleasesAsync(teardownToken).ConfigureAwait(false);
                if (!flushed || _leaseManager.HasFailedReleases)
                {
                    success = false;
                }
            }
            catch
            {
                success = false;
            }
        }
        }
        finally
        {
            localTimeoutCts?.Dispose();
        }

        return success;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var businessCancelTask = TriggerBusinessCancellation();
        Task? teardownTask = null;
        Task? progTask = null;
        Task? connTask = null;
        Task? pipeTask = null;

        try
        {
            try { _pumpCts.Cancel(); } catch { }

            using var overallCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var overallToken = overallCts.Token;

            // 1. 统一复用已持有或执行收尾任务
            teardownTask = EnsureTeardownAsync();
            try { await teardownTask.WaitAsync(overallToken).ConfigureAwait(false); } catch { }

            // 2. 进度通道收尾
            if (_sessionProgressChannel is not null)
            {
                progTask = _sessionProgressChannel.DisposeAsync().AsTask();
                _ = progTask.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
                try { await progTask.WaitAsync(overallToken).ConfigureAwait(false); } catch { }
            }

            // 3. 入站分发泵收尾
            if (_inboundPumpTask is not null)
            {
                _ = _inboundPumpTask.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
                try { await _inboundPumpTask.WaitAsync(overallToken).ConfigureAwait(false); } catch { }
            }
        }
        finally
        {
            // 底层通信与连接强制关闭（1 秒强制关闭预算）
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var closeToken = closeCts.Token;

            if (_connection is not null)
            {
                connTask = _connection.DisposeAsync().AsTask();
                _ = connTask.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
                try { await connTask.WaitAsync(closeToken).ConfigureAwait(false); } catch { }
            }

            if (_pipeClient is not null)
            {
                pipeTask = _pipeClient.DisposeAsync().AsTask();
                _ = pipeTask.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
                try { await pipeTask.WaitAsync(closeToken).ConfigureAwait(false); } catch { }
            }

            // 取消源释放：由后台任务持续等待所有使用者（包含固定基础任务与动态登记、操作释放及尝试退出任务）物理完全退出后再释放
            _ = WaitForAllSessionUsersAndDisposeCtsAsync(
                this,
                businessCancelTask,
                _inboundPumpTask,
                teardownTask,
                progTask,
                connTask,
                pipeTask);
        }
    }

    /// <summary>
    /// 在后台循环等待会话所有使用者（基础收尾任务、动态登记中任务、操作释放任务以及尝试物理退出任务）完全退出后，安全释放全部取消源。
    /// 采用动态循环核验消除瞬时快照竞态，杜绝取消源在迟到清理任务完成前被释放。
    /// </summary>
    private static async Task WaitForAllSessionUsersAndDisposeCtsAsync(
        AbilitySession session,
        Task businessCancelTask,
        Task? inboundPumpTask,
        Task? teardownTask,
        Task? progTask,
        Task? connTask,
        Task? pipeTask)
    {
        // 1. 等待固定基础任务结束
        var fixedTasks = new List<Task> { businessCancelTask };
        if (inboundPumpTask is not null) fixedTasks.Add(inboundPumpTask);
        if (teardownTask is not null) fixedTasks.Add(teardownTask);
        if (progTask is not null) fixedTasks.Add(progTask);
        if (connTask is not null) fixedTasks.Add(connTask);
        if (pipeTask is not null) fixedTasks.Add(pipeTask);

        try
        {
            await Task.WhenAll(fixedTasks).ConfigureAwait(false);
        }
        catch
        {
            // 吸收基础任务异常，继续等待动态生命周期任务物理退出
        }

        // 2. 状态驱动等待所有在途登记、操作释放及尝试物理退出任务完全归零（彻底消除 WhenAny 忙循环）
        try
        {
            await session.WaitForZeroSessionUsersAsync().ConfigureAwait(false);
        }
        catch
        {
            // 吸收异常继续推进释放
        }

        // 3. 所有使用者物理完全退出，释放会话持有的取消源
        try { session._businessCts.Dispose(); } catch { }
        try { session._pumpCts.Dispose(); } catch { }
        try { session._registrationClosingCts.Dispose(); } catch { }
        try { session._terminalInterruptCts.Dispose(); } catch { }
    }

    /// <summary>
    /// 向宿主登记受管操作，并在会话生命周期与收尾裁决门控下完成双重核验与迟到清理。
    /// 在同一次锁内裁决中取得登记过程的所有权，确保取消源释放必须等待所有在途登记物理退出。
    /// </summary>
    /// <param name="name">操作名称。</param>
    /// <param name="purpose">操作业务目标说明。</param>
    /// <param name="suggestedPolicy">建议的初始运行策略。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <param name="timeProvider">时间提供器。</param>
    /// <param name="progress">操作级进度汇报接口。</param>
    /// <param name="parent">可选父级受管操作。</param>
    /// <returns>构建并注册完成的受管操作实例。</returns>
    internal async Task<ManagedOperation> RegisterOperationAsync(
        string name,
        string purpose,
        RunPolicyMdl suggestedPolicy,
        CancellationToken cancellationToken,
        TimeProvider timeProvider,
        IProgress<RunProgress>? progress,
        ManagedOperation? parent)
    {
        lock (_operationsGate)
        {
            if (_operationsClosed || Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(AbilitySession), "会话已关闭，拒绝登记新操作。");
            }

            EnterSessionUserLocked();
        }

        try
        {
            return await ExecuteRegisterOperationAsync(
                name, purpose, suggestedPolicy, cancellationToken, timeProvider, progress, parent).ConfigureAwait(false);
        }
        finally
        {
            LeaveSessionUser();
        }
    }

    /// <summary>
    /// 实际执行受管操作登记网络请求与双重核验的异步工作流。
    /// </summary>
    private async Task<ManagedOperation> ExecuteRegisterOperationAsync(
        string name,
        string purpose,
        RunPolicyMdl suggestedPolicy,
        CancellationToken cancellationToken,
        TimeProvider timeProvider,
        IProgress<RunProgress>? progress,
        ManagedOperation? parent)
    {
        var connection = RequireConnection();
        var requestId = connection.NextRequestId();
        var operationKey = "op-" + Guid.NewGuid().ToString("N");
        var request = ProtocolMessages.CreateOperationRegister(
            requestId, operationKey, parent?.OperationKey, name, purpose, suggestedPolicy.ToBody());

        var pendingTcs = new TaskCompletionSource<ManagedOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = pendingTcs.Task.ContinueWith(
            static task => { _ = task.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        lock (_operationsGate)
        {
            if (_operationsClosed || Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(AbilitySession), "会话已关闭，拒绝登记新操作。");
            }
            _pendingRegistrations[operationKey] = pendingTcs;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _registrationClosingCts.Token);
        try
        {
            var response = await connection.RequestAsync(request, linkedCts.Token).ConfigureAwait(false);

            if (string.Equals(response.Type, ProtocolConstants.MessageTypes.RequestFault, StringComparison.Ordinal))
            {
                var fault = ProtocolMessages.ReadBody<RequestFaultBody>(response);
                throw new AbilityRequestFaultException(fault.Kind, fault.Message, site: "OperationRegister");
            }

            if (!string.Equals(response.Type, ProtocolConstants.MessageTypes.OperationAccepted, StringComparison.Ordinal))
            {
                throw new ProtocolException($"受管操作登记收到意外答复 '{response.Type}'。");
            }

            var accepted = ProtocolMessages.ReadBody<OperationAcceptedBody>(response);
            var effectivePolicy = accepted.Policy is not null
                ? RunPolicyMdl.FromBody(accepted.Policy)
                : suggestedPolicy;

            var operation = new ManagedOperation(
                (envelope, ct) => connection.SendAsync(envelope, ct),
                key => _operations.TryRemove(key, out _),
                operationKey,
                name,
                purpose,
                effectivePolicy,
                accepted.PolicyVersion,
                accepted.PolicySource,
                cancellationToken,
                _businessCts.Token,
                timeProvider,
                _cleanupGracePeriod,
                progress,
                TrackAttemptExitTask);

            lock (_operationsGate)
            {
                if (_operationsClosed || Volatile.Read(ref _disposed) != 0)
                {
                    var lateDisposeTask = Task.Run(async () => await operation.DisposeAsync().ConfigureAwait(false));
                    TrackOperationDisposalTask(lateDisposeTask);
                    throw new ObjectDisposedException(nameof(AbilitySession), "会话已关闭，迟到批准的操作被拒绝并释放。");
                }

                _operations.TryAdd(operationKey, operation);
                pendingTcs.TrySetResult(operation);
                return operation;
            }
        }
        catch (OperationCanceledException) when (_registrationClosingCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var disposedEx = new ObjectDisposedException(nameof(AbilitySession), "会话已关闭，登记请求已中止。");
            pendingTcs.TrySetException(disposedEx);
            throw disposedEx;
        }
        catch (Exception ex)
        {
            pendingTcs.TrySetException(ex);
            throw;
        }
        finally
        {
            _pendingRegistrations.TryRemove(operationKey, out _);
        }
    }

    /// <summary>
    /// 在共享 3 秒总预算内按顺序收拢在途枚举推进、异步释放枚举器并执行会话必要收尾。
    /// 确保 MoveNextAsync 与 DisposeAsync 绝不并发，且预算耗尽仅结束前台等待，后台工作继续由会话持有追踪。
    /// </summary>
    /// <typeparam name="TOutput">流元素类型。</typeparam>
    /// <param name="enumerator">异步流枚举器。</param>
    /// <param name="moveNextLock">推进状态互斥同步锁。</param>
    /// <param name="inFlightMoveNext">当前在途的推进任务引用（若有）。</param>
    /// <param name="budgetToken">共享 3 秒收尾预算取消令牌。</param>
    /// <returns>收尾与清理是否完全成功。</returns>
    private async Task<bool> CleanUpEnumeratorAndTeardownAsync<TOutput>(
        IAsyncEnumerator<TOutput> enumerator,
        object moveNextLock,
        Task<bool>? inFlightMoveNext,
        CancellationToken budgetToken)
    {
        var success = true;

        // 1. 触发业务取消，通知在途推进尽快退出
        _ = TriggerBusinessCancellation();

        // 2. 检查并等待在途 MoveNextAsync 完成，杜绝与 DisposeAsync 并发
        Task<bool>? runningMoveNext;
        lock (moveNextLock)
        {
            runningMoveNext = inFlightMoveNext;
        }

        var moveNextExited = true;
        if (runningMoveNext is not null && !runningMoveNext.IsCompleted)
        {
            try
            {
                await runningMoveNext.WaitAsync(budgetToken).ConfigureAwait(false);
            }
            catch
            {
                // 捕获等待中的取消或故障，真实退出由任务 IsCompleted 状态权威确认
            }

            // 必须以真实任务完成状态确认退出；预算取消绝不能误判为推进已退出
            if (!runningMoveNext.IsCompleted)
            {
                moveNextExited = false;
                success = false;
            }
        }

        // 3. 释放枚举器：
        // 若 MoveNextAsync 在预算内已完成，前台在同一预算剩余时间内执行 DisposeAsync；
        // 若 MoveNextAsync 尚未退出（预算耗尽），绝不并发调用 DisposeAsync，交由会话后台追踪任务在 MoveNextAsync 完成后再释放。
        if (moveNextExited)
        {
            var disposeTask = Task.Run(async () =>
            {
                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AbilityDiagnostics.Log("Warning", DiagnosticCategory, 0, $"[Ability.Sdk] 枚举器释放异常: {AbilityDiagnostics.ExceptionSummary(ex)}");
                    throw;
                }
            });

            TrackSessionWork(disposeTask);

            try
            {
                await disposeTask.WaitAsync(budgetToken).ConfigureAwait(false);
            }
            catch
            {
                success = false;
            }
        }
        else if (runningMoveNext is not null)
        {
            // 推进尚未退出：安排后台排队在推进实际退出后再释放枚举器，纳入会话生命周期持有
            var deferredDisposeTask = Task.Run(async () =>
            {
                try
                {
                    await runningMoveNext.ConfigureAwait(false);
                }
                catch
                {
                }
                finally
                {
                    try
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        AbilityDiagnostics.Log("Warning", DiagnosticCategory, 0, $"[Ability.Sdk] 迟到枚举器释放异常: {AbilityDiagnostics.ExceptionSummary(ex)}");
                    }
                }
            });

            TrackSessionWork(deferredDisposeTask);
        }

        // 4. 会话必要收尾：在同一共享预算剩余时间内执行
        var teardownSuccess = await EnsureTeardownAsync(budgetToken).ConfigureAwait(false);
        if (!teardownSuccess)
        {
            success = false;
        }

        return success;
    }
}
