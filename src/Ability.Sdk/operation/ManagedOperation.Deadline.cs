using Ability.Core;
using Ability.Model;

namespace Ability.Sdk;

/// <summary>
/// 受管操作的策略期限监听实现。
/// </summary>
public sealed partial class ManagedOperation
{
    /// <summary>
    /// 后台监听当前尝试的超时期限与动态策略变更。
    /// 期限到达后立即取消当前尝试，通知写出绝不作为取消生效的前提。
    /// </summary>
    /// <param name="attemptStart">当前尝试开始时的单调时间戳。</param>
    /// <param name="attemptCancellation">当前尝试的业务取消源。</param>
    /// <param name="cancellationToken">期限监听的父级取消令牌。</param>
    /// <returns>期限动作是否已终结当前尝试。</returns>
    private async Task<bool> WatchDeadlineAsync(
        long attemptStart,
        CancellationTokenSource attemptCancellation,
        CancellationToken cancellationToken)
    {
        var notifiedVersion = long.MinValue;

        while (true)
        {
            RunPolicyMdl policy;
            long anchor;
            long version;
            string source;
            Task changed;

            lock (_policyGate)
            {
                if (_attemptState != 1 || _ended != 0)
                {
                    return false;
                }

                policy = _policy;
                version = _policyVersion;
                source = _policySource;
                anchor = policy.DeadlineReference == DeadlineReference.FromStart
                    ? attemptStart
                    : _updatedAt;
                changed = _policyChanged.Task;
            }

            if (policy.Deadline is not { } deadline)
            {
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var remaining = deadline - _timeProvider.GetElapsedTime(anchor);
            if (remaining > TimeSpan.Zero)
            {
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delay = Task.Delay(remaining, _timeProvider, delayCts.Token);
                var winner = await Task.WhenAny(delay, changed).ConfigureAwait(false);
                if (winner == delay)
                {
                    await delay.ConfigureAwait(false);
                    continue;
                }

                delayCts.Cancel();
                try
                {
                    await delay.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (delayCts.IsCancellationRequested)
                {
                    // 策略版本变化或父级取消结束本轮有限期限等待。
                }

                await changed.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                continue;
            }

            lock (_policyGate)
            {
                if (_attemptState != 1 || _ended != 0)
                {
                    return false;
                }

                if (_policyVersion != version)
                {
                    continue;
                }

                if (policy.DeadlineAction == DeadlineAction.Cancel)
                {
                    _attemptState = 2;
                    _expired = 1;
                    _progress.DiscardPending();
                }
                else if (notifiedVersion != version)
                {
                    _expired = 1;
                    notifiedVersion = version;
                }
                else
                {
                    changed = _policyChanged.Task;
                }
            }

            if (policy.DeadlineAction == DeadlineAction.Cancel)
            {
                lock (_policyGate)
                {
                    if (_policyVersion != version || _attemptState != 2)
                    {
                        continue;
                    }
                }

                // 局部期限到达后立即异步隔离取消本次尝试，杜绝外部业务回调阻塞到期通知写出
                _ = TriggerAttemptCancellation(attemptCancellation);

                var actionText = policy.DeadlineAction == DeadlineAction.Remind ? "Notify" : "Stop";
                var expiredMessage = ProtocolMessages.CreateOperationExpired(
                    OperationKey,
                    version,
                    source,
                    actionText);

                _ = ObserveWriteAsync(SendEnvelopeAsync(expiredMessage));
                return true;
            }

            if (notifiedVersion == version)
            {
                var actionText = policy.DeadlineAction == DeadlineAction.Remind ? "Notify" : "Stop";
                var expiredMessage = ProtocolMessages.CreateOperationExpired(
                    OperationKey,
                    version,
                    source,
                    actionText);

                _ = ObserveWriteAsync(SendEnvelopeAsync(expiredMessage));
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 观察异步写出任务的结果，避免未观察的后台异常。
    /// </summary>
    /// <param name="writeTask">写出任务。</param>
    private static async Task ObserveWriteAsync(Task writeTask)
    {
        try
        {
            await writeTask.ConfigureAwait(false);
        }
        catch
        {
            // 写帧失败由底层连接与诊断记录，不影响取消流程推进
        }
    }
}
