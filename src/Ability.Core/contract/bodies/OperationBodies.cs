namespace Ability.Core;

/// <summary>
/// 通用运行策略定义体；约束操作的超时、重试与进度汇报。
/// </summary>
/// <param name="Deadline">业务期限；为空表示无业务超时，仍受有限系统兜底保护。</param>
/// <param name="DeadlineReference">计时起点参考，如 "FromStart" 或 "FromUpdate"。</param>
/// <param name="DeadlineAction">到期动作，如 "Remind" 或 "Cancel"。</param>
/// <param name="MaxAttempts">最大尝试次数上限（含首次尝试，即 maxRetries + 1）。</param>
/// <param name="ProgressInterval">进度合并上报的时间间隔。</param>
public sealed record RunPolicyBody(
    TimeSpan? Deadline,
    string DeadlineReference,
    string DeadlineAction,
    int MaxAttempts,
    TimeSpan ProgressInterval)
{
    /// <summary>
    /// 获取缺省运行策略（无业务期限、自尝试开始计时、到期动作 Stop、单次尝试、250ms 进度间隔）。
    /// </summary>
    public static RunPolicyBody Default { get; } = new(null, "FromStart", "Stop", 1, TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// 校验策略协议体的各字段数值与枚举合法性。
    /// 兼容旧 v3 协议动作与新枚举定义。
    /// </summary>
    /// <param name="body">待校验的运行策略数据体。</param>
    /// <param name="error">若校验不通过输出错误原因说明。</param>
    /// <returns>合法返回 true；否则返回 false。</returns>
    public static bool Validate(RunPolicyBody? body, out string? error)
    {
        if (body is null)
        {
            error = "运行策略体不可为空。";
            return false;
        }

        if (body.Deadline is { } deadline && deadline <= TimeSpan.Zero)
        {
            error = $"Deadline 必须为正时间跨度，实际为：{deadline}。";
            return false;
        }

        if (!string.Equals(body.DeadlineReference, "FromStart", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(body.DeadlineReference, "FromUpdate", StringComparison.OrdinalIgnoreCase))
        {
            error = $"不支持的 DeadlineReference 起点参考：'{body.DeadlineReference}'。";
            return false;
        }

        if (!string.Equals(body.DeadlineAction, "Remind", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(body.DeadlineAction, "Notify", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(body.DeadlineAction, "Cancel", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(body.DeadlineAction, "Stop", StringComparison.OrdinalIgnoreCase))
        {
            error = $"不支持的 DeadlineAction 到期动作：'{body.DeadlineAction}'。";
            return false;
        }

        if (body.MaxAttempts < 1)
        {
            error = $"MaxAttempts 必须大于等于 1，实际为：{body.MaxAttempts}。";
            return false;
        }

        if (body.ProgressInterval < TimeSpan.Zero)
        {
            error = $"ProgressInterval 不可为负时间跨度，实际为：{body.ProgressInterval}。";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// 将策略体的到期动作转换为与旧 v3 线协议完全兼容的表示（"Notify" 或 "Stop"）。
    /// </summary>
    /// <returns>动作转换为 v3 格式的策略体副本。</returns>
    public RunPolicyBody ToWireV3()
    {
        var wireAction = string.Equals(DeadlineAction, "Remind", StringComparison.OrdinalIgnoreCase)
            || string.Equals(DeadlineAction, "Notify", StringComparison.OrdinalIgnoreCase)
            ? "Notify"
            : "Stop";
        return this with { DeadlineAction = wireAction };
    }
}

/// <summary>
/// 受管操作登记请求体；由技能向上游声明一个可观测操作并给出建议策略。
/// </summary>
/// <param name="OperationKey">操作的唯一标识键。</param>
/// <param name="ParentOperationKey">父操作键；为空表示根操作。</param>
/// <param name="Name">操作的人类可读名称。</param>
/// <param name="Purpose">操作的目标用途说明。</param>
/// <param name="SuggestedPolicy">技能建议的运行策略。</param>
public sealed record OperationRegisterBody(
    string OperationKey,
    string? ParentOperationKey,
    string Name,
    string Purpose,
    RunPolicyBody SuggestedPolicy);

/// <summary>
/// 受管操作登记接受答复体；确认登记并下发最终生效的权威策略。
/// </summary>
/// <param name="OperationKey">操作的唯一标识键。</param>
/// <param name="PolicyVersion">生效策略的版本号。</param>
/// <param name="PolicySource">策略来源，如 "Default"、"SkillSuggested"、"HostOverride"。</param>
/// <param name="Policy">最终生效的运行策略。</param>
public sealed record OperationAcceptedBody(
    string OperationKey,
    long PolicyVersion,
    string PolicySource,
    RunPolicyBody Policy);

/// <summary>
/// 运行策略动态更新体；由宿主主动下发，包含单调递增的版本号。
/// </summary>
/// <param name="OperationKey">目标操作的标识键。</param>
/// <param name="Version">新策略版本号。</param>
/// <param name="Source">更新发起方来源说明。</param>
/// <param name="Policy">更新后的新策略内容。</param>
public sealed record PolicyUpdateBody(
    string OperationKey,
    long Version,
    string Source,
    RunPolicyBody Policy);

/// <summary>
/// 运行策略更新应用确认体；技能在串行裁决应用新策略后回复。
/// </summary>
/// <param name="OperationKey">目标操作的标识键。</param>
/// <param name="Version">已确认应用或忽略的策略版本号。</param>
/// <param name="Source">策略来源。</param>
/// <param name="Outcome">应用结果，如 "Applied"、"Rejected"、"AlreadyCompleted"。</param>
public sealed record PolicyAppliedBody(
    string OperationKey,
    long Version,
    string Source,
    string Outcome);

/// <summary>
/// 受管操作生命周期与状态通知体；记录阶段转换、当前尝试及解释说明。
/// </summary>
/// <param name="OperationKey">操作标识键。</param>
/// <param name="Attempts">当前累计尝试次数。</param>
/// <param name="State">操作状态，如 "Running"、"Succeeded"、"Failed"、"Expired"。</param>
/// <param name="Reason">状态原因详细说明；可选。</param>
public sealed record OperationStateBody(
    string OperationKey,
    int Attempts,
    string State,
    string? Reason);

/// <summary>
/// 受管操作超时到期通知体；由负责计时的宿主或上游发出。
/// </summary>
/// <param name="OperationKey">操作标识键。</param>
/// <param name="PolicyVersion">触发到期时依据的策略版本号。</param>
/// <param name="Source">计时来源。</param>
/// <param name="DeadlineAction">到期执行动作，如 "Remind" 或 "Cancel"。</param>
public sealed record OperationExpiredBody(
    string OperationKey,
    long PolicyVersion,
    string Source,
    string DeadlineAction);

/// <summary>
/// 结构化操作进度通知体；用于在长任务中上报可度量的进度数据。
/// </summary>
/// <param name="OperationKey">操作标识键。</param>
/// <param name="Sequence">单调递增的进度序号（必须为正数）。</param>
/// <param name="Stage">当前处理阶段名称。</param>
/// <param name="Completed">已完成数量；若无法计算可为 null。</param>
/// <param name="Total">总量；若总量未知可为 null。</param>
/// <param name="Unit">数量单位（如 "items"、"bytes"）。</param>
/// <param name="Message">附带的简短说明文本。</param>
public sealed record ProgressBody(
    string OperationKey,
    long Sequence,
    string Stage,
    long? Completed,
    long? Total,
    string Unit,
    string? Message);
