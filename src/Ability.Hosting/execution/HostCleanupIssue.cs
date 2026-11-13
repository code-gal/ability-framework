namespace Ability.Hosting;

/// <summary>
/// 记录宿主收尾阶段发生的问题或异常信息（已脱敏）。
/// </summary>
/// <param name="Stage">发生阶段（如 "Preparation"、"Execution"、"Consumer"、"Deletion"、"Governance"、"Diagnostics"、"Cleanup" 等）。</param>
/// <param name="Kind">错误种类分类。</param>
/// <param name="Message">脱敏说明信息。</param>
public sealed record HostCleanupIssue(string Stage, string Kind, string Message);

