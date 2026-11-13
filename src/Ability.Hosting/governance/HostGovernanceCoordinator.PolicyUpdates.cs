namespace Ability.Hosting;

/// <summary>
/// 处理策略更新发送前后的未决记录收口。
/// </summary>
internal sealed partial class HostGovernanceCoordinator
{
    /// <summary>
    /// 在策略更新未发送成功时，以单次原子裁决移除未决记录并记录发送结果。
    /// </summary>
    /// <param name="requestId">策略更新请求编号。</param>
    /// <param name="outcome">具体发送结果，如拒绝、失败或取消。</param>
    /// <returns>本次调用若赢得完成裁决并清除记录则返回 true。</returns>
    public bool HandlePolicySendFailure(long requestId, string outcome)
    {
        if (!_pendingPolicyUpdates.TryGetValue(requestId, out var pendingUpdate)
            || !pendingUpdate.TryComplete())
        {
            return false;
        }

        _pendingPolicyUpdates.TryRemove(requestId, out _);
        pendingUpdate.CancelTimeout();
        if (_operations.TryGetValue(pendingUpdate.OperationKey, out var operation))
        {
            operation.RecordPolicyUpdateOutcome(outcome, pendingUpdate.Version);
        }

        return true;
    }
}
