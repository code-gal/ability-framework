namespace Ability.Core;

/// <summary>
/// 单项 Schema 验证违规记录。
/// 记录数据实例中的路径、违反的关键字以及固定原因说明。
/// </summary>
/// <param name="Path">数据实例中的违规路径（如 "$.items[2].price"）。</param>
/// <param name="Keyword">所违反的 Schema 约束关键字（如 "minimum"、"required"）。</param>
/// <param name="Message">不含敏感实例数据的固定违规原因说明。</param>
public sealed record SchemaViolation(string Path, string Keyword, string Message);

/// <summary>
/// Schema 校验的汇总结果。
/// </summary>
/// <param name="IsValid">数据是否完全符合 Schema 约束。</param>
/// <param name="Violations">收集到的全部违规项列表；当通过时为空列表。</param>
public sealed record SchemaValidationResult(bool IsValid, IReadOnlyList<SchemaViolation> Violations)
{
    /// <summary>
    /// 将全部违规项拼接为单行紧凑文本，供诊断和日志输出使用。
    /// </summary>
    public string Summary => string.Join("；", Violations.Select(item => $"{item.Path} {item.Keyword}：{item.Message}"));
}
