namespace Ability.Core;

/// <summary>
/// 跨进程结构化诊断常量定义。
/// </summary>
public static class DiagnosticConstants
{
    /// <summary>
    /// 宿主用来识别结构化诊断行的固定前缀。
    /// </summary>
    public const string Marker = "skill-diag:";

    /// <summary>
    /// 当前结构化诊断格式版本号。
    /// </summary>
    public const int DiagnosticVersion = 1;

    /// <summary>
    /// 兼容旧版实现的 SDK 名称。
    /// </summary>
    public const string LegacySdkName = "SkillIpcMvp";

    /// <summary>
    /// 当前 Ability Framework 的 SDK 名称。
    /// </summary>
    public const string CurrentSdkName = "AbilityFramework";
}

/// <summary>
/// 跨进程传递的结构化诊断记录契约。
/// 技能端输出至 stderr，宿主端解析并聚合，两端共享同一数据模型。
/// </summary>
public sealed class DiagnosticRecord
{
    /// <summary>
    /// 诊断契约格式版本号（当前固定为 1）。
    /// </summary>
    public int DiagnosticVersion { get; init; } = DiagnosticConstants.DiagnosticVersion;

    /// <summary>
    /// 生成该诊断记录的 SDK 名称标识（默认 "AbilityFramework"；注意旧版宿主解析器只接受 "SkillIpcMvp"，新 Hosting 对两种标识的接收策略在后续诊断迁移阶段落实）。
    /// </summary>
    public string Sdk { get; init; } = DiagnosticConstants.CurrentSdkName;

    /// <summary>
    /// 记录类别；支持 "log"、"span_start"、"span_end"。
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// 诊断或 Span 名称。
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 诊断源名称（ActivitySource 或 Logger 类别）。
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// W3C Trace ID 标识。
    /// </summary>
    public string TraceId { get; init; } = string.Empty;

    /// <summary>
    /// 当前 Span ID 标识。
    /// </summary>
    public string SpanId { get; init; } = string.Empty;

    /// <summary>
    /// 父级 Span ID 标识；根 Span 为空。
    /// </summary>
    public string ParentSpanId { get; init; } = string.Empty;

    /// <summary>
    /// 开始时间的 Unix 纳秒时间戳。
    /// </summary>
    public long StartTimeUnixNano { get; init; }

    /// <summary>
    /// 结束时间的 Unix 纳秒时间戳；未结束时为 0。
    /// </summary>
    public long EndTimeUnixNano { get; init; }

    /// <summary>
    /// Span 状态，例如 "Unset"、"Ok"、"Error"。
    /// </summary>
    public string Status { get; init; } = "Unset";

    /// <summary>
    /// Span 状态描述文本。
    /// </summary>
    public string StatusDescription { get; init; } = string.Empty;

    /// <summary>
    /// 诊断时的 ActivityKind 枚举名称。
    /// </summary>
    public string ActivityKind { get; init; } = string.Empty;

    /// <summary>
    /// 已脱敏的键值标签字典。
    /// </summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// 日志级别（仅当 Kind 为 "log" 时有效）。
    /// </summary>
    public string Level { get; init; } = string.Empty;

    /// <summary>
    /// 日志分类。
    /// </summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>
    /// 日志事件标识 ID。
    /// </summary>
    public int EventId { get; init; }

    /// <summary>
    /// 诊断消息内容。
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// 记录生成时间的 Unix 纳秒时间戳。
    /// </summary>
    public long TimeUnixNano { get; init; }
}
