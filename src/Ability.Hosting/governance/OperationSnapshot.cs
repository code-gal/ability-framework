using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// 受管操作在某一时刻的完整只读快照。
/// 包含操作标识、生命周期状态、累计尝试次数、当前权威策略、耗时、停止原因及最新进度等。
/// 当操作状态、进度或策略变更时由宿主生成并通知外部观察者。
/// </summary>
/// <param name="OperationKey">受管操作唯一标识键。</param>
/// <param name="ParentOperationKey">父操作唯一标识键；为 null 表示根操作。</param>
/// <param name="Name">操作人类可读名称。</param>
/// <param name="Purpose">操作业务用途说明。</param>
/// <param name="State">当前生命周期状态（Registered、Running、Succeeded、Failed、Unresponsive、Stopped 等）。</param>
/// <param name="Attempts">当前累计发起的尝试次数。</param>
/// <param name="PolicyVersion">当前生效策略的版本号。</param>
/// <param name="PolicySource">当前生效策略来源标记（如 SkillSuggested、HostOverride 等）。</param>
/// <param name="Policy">当前生效的运行策略内容。</param>
/// <param name="RegisteredAt">操作初次登记的绝对时间点。</param>
/// <param name="StartedAt">操作首次开始执行的绝对时间点；尚未开始时为 null。</param>
/// <param name="EndedAt">操作整体结束的绝对时间点；仍在运行或尚未开始时为 null。</param>
/// <param name="Elapsed">自登记起到快照生成时累计流逝的时间跨度。</param>
/// <param name="StopReason">操作终止或停止的具体原因说明。</param>
/// <param name="LatestProgress">最新采纳的结构化进度数据；无进度时为 null。</param>
/// <param name="LastPolicyUpdateOutcome">最近一次策略更新的裁决结果（如 Applied、Stale 等）。</param>
/// <param name="LastPolicyUpdateVersion">最近一次策略更新的版本号。</param>
/// <param name="LastPolicyUpdateSource">最近一次策略更新的发起来源。</param>
public sealed record OperationSnapshot(
    string OperationKey,
    string? ParentOperationKey,
    string Name,
    string Purpose,
    string State,
    int Attempts,
    long PolicyVersion,
    string PolicySource,
    RunPolicyBody Policy,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    TimeSpan Elapsed,
    string? StopReason,
    ProgressBody? LatestProgress,
    string? LastPolicyUpdateOutcome = null,
    long? LastPolicyUpdateVersion = null,
    string? LastPolicyUpdateSource = null);
