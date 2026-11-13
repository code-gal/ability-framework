namespace Ability.Hosting;

/// <summary>
/// 任务专属临时目录的生命周期收尾报告。
/// 分别记录目录标记状态、转存消费、物理删除及最终处置事实。
/// </summary>
public sealed class TaskTempReport
{
    /// <summary>
    /// 任务临时目录的绝对路径。
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// 目录创建的 UTC 时间字符串。
    /// </summary>
    public string CreatedUtc { get; init; } = string.Empty;

    /// <summary>
    /// 内存中的目录标记状态：active、pending、releasing 或 unconfirmed。
    /// </summary>
    public string State { get; init; } = string.Empty;

    /// <summary>
    /// 磁盘 task.owner.json 文件中实际最后一次成功持久化的标记状态（写入失败时保留旧值或为 null）。
    /// </summary>
    public string? PersistedState { get; init; }

    /// <summary>
    /// 转存消费执行状态：NotRequested、Pending、Succeeded、Failed 或 TimedOut。
    /// </summary>
    public string ConsumptionStatus { get; init; } = "NotRequested";

    /// <summary>
    /// 物理删除执行状态：NotRequested、Pending、Succeeded 或 Failed。
    /// </summary>
    public string DeletionStatus { get; init; } = "NotRequested";

    /// <summary>
    /// 本次临时目录处置事实：RetainedByPolicy、Deleted、ConsumerFailed、DeleteFailed、AwaitingProcessExit 或 OwnershipUnconfirmed。
    /// </summary>
    public string Disposition { get; init; } = "RetainedByPolicy";

    /// <summary>
    /// 保留原因说明；成功删除时为 null。
    /// </summary>
    public string? RetentionReason { get; init; }

    /// <summary>
    /// 目录是否已被物理删除成功。
    /// </summary>
    public bool Deleted => string.Equals(DeletionStatus, "Succeeded", StringComparison.Ordinal);

    /// <summary>
    /// 兼容旧属性名的保留原因获取。
    /// </summary>
    public string? RetainedReason => RetentionReason;

    /// <summary>
    /// 所有者标记文件是否已成功持久化到磁盘。
    /// </summary>
    public bool MarkerPersisted { get; init; }

    /// <summary>
    /// 若写入标记文件失败，记录具体的脱敏错误信息；成功时为 null。
    /// </summary>
    public string? MarkerError { get; init; }

    /// <summary>
    /// 创建当前报告的独立快照副本，防止后续修改影响前台已返回结果。
    /// </summary>
    /// <returns>当前报告的快照副本。</returns>
    public TaskTempReport Snapshot() => new()
    {
        Path = Path,
        CreatedUtc = CreatedUtc,
        State = State,
        PersistedState = PersistedState,
        ConsumptionStatus = ConsumptionStatus,
        DeletionStatus = DeletionStatus,
        Disposition = Disposition,
        RetentionReason = RetentionReason,
        MarkerPersisted = MarkerPersisted,
        MarkerError = MarkerError
    };
}
