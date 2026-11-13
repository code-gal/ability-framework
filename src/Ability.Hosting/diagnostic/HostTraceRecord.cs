using System.Text.Json;
using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// 一条来自技能进程 stderr 的结构化诊断记录。
/// 宿主端仅解析符合固定标记与格式的行，畸形行降级为普通文本，两者不混淆。
/// </summary>
internal sealed class HostTraceRecord
{
    /// <summary>
    /// 结构化诊断记录的固定前缀（与 Core 契约对齐）。
    /// </summary>
    internal const string Marker = DiagnosticConstants.Marker;

    /// <summary>
    /// 记录类别（"log"、"span_start"、"span_end"）。
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
    /// 父级 Span ID 标识。
    /// </summary>
    public string ParentSpanId { get; init; } = string.Empty;

    /// <summary>
    /// W3C TraceState 上下文字符串。
    /// </summary>
    public string TraceState { get; init; } = string.Empty;

    /// <summary>
    /// 开始时间的 Unix 纳秒时间戳。
    /// </summary>
    public long StartTimeUnixNano { get; init; }

    /// <summary>
    /// 结束时间的 Unix 纳秒时间戳。
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
    /// 记录时的 ActivityKind 枚举名称。
    /// </summary>
    public string ActivityKind { get; init; } = string.Empty;

    /// <summary>
    /// 附加诊断标签字典。
    /// </summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// 日志级别名称。
    /// </summary>
    public string Level { get; init; } = string.Empty;

    /// <summary>
    /// 日志分类名称。
    /// </summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>
    /// 日志事件 ID。
    /// </summary>
    public int EventId { get; init; }

    /// <summary>
    /// 诊断消息或日志正文。
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// 日志时间的 Unix 纳秒时间戳。
    /// </summary>
    public long TimeUnixNano { get; init; }

    /// <summary>
    /// 解析一行结构化诊断。
    /// 标记不符、JSON 畸形、字段缺失或 SDK 不匹配时降级返回 null，由调用方按普通文本处理。
    /// 同时兼容接受新版 "AbilityFramework" 与旧版 "SkillIpcMvp" 标识。
    /// </summary>
    /// <param name="line">待解析的 stderr 单行文本。</param>
    /// <returns>解析成功的结构化记录；无法识别时返回 null。</returns>
    internal static HostTraceRecord? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith(Marker, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line[Marker.Length..]);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !TryReadInt32(root, "diagnosticVersion", out var parsedVersion)
                || parsedVersion != 1
                || !TryReadString(root, "sdk", out var sdk)
                || !IsSupportedSdk(sdk)
                || !TryReadString(root, "kind", out var kindName)
                || kindName is not ("log" or "span_start" or "span_end"))
            {
                return null;
            }

            var traceId = ReadString(root, "traceId");
            var spanId = ReadString(root, "spanId");
            var parentSpanId = ReadString(root, "parentSpanId");
            var traceState = ReadString(root, "traceState");

            if (kindName is "span_start" or "span_end")
            {
                if (!IsValidTraceId(traceId) || !IsValidSpanId(spanId))
                {
                    return null;
                }

                if (!string.IsNullOrEmpty(parentSpanId) && !IsValidSpanId(parentSpanId))
                {
                    return null;
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(traceId) && !IsValidTraceId(traceId))
                {
                    return null;
                }

                if (!string.IsNullOrEmpty(spanId) && !IsValidSpanId(spanId))
                {
                    return null;
                }
            }

            return new HostTraceRecord
            {
                Kind = kindName,
                Name = ReadString(root, "name"),
                Source = ReadString(root, "source"),
                TraceId = traceId,
                SpanId = spanId,
                ParentSpanId = parentSpanId,
                TraceState = traceState,
                StartTimeUnixNano = ReadInt64(root, "startTimeUnixNano"),
                EndTimeUnixNano = ReadInt64(root, "endTimeUnixNano"),
                Status = ReadString(root, "status") is { Length: > 0 } status ? status : "Unset",
                StatusDescription = ReadString(root, "statusDescription"),
                ActivityKind = ReadString(root, "activityKind"),
                Tags = ReadTags(root),
                Level = ReadString(root, "level"),
                Category = ReadString(root, "category"),
                EventId = TryReadInt32(root, "eventId", out var eventId) ? eventId : 0,
                Message = ReadString(root, "message"),
                TimeUnixNano = ReadInt64(root, "timeUnixNano")
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 校验 TraceId 是否符合 W3C 规范（32 位小写十六进制且非全零）。
    /// </summary>
    private static bool IsValidTraceId(string traceId) =>
        traceId.Length == 32
        && traceId.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
        && !traceId.All(c => c == '0');

    /// <summary>
    /// 校验 SpanId 是否符合 W3C 规范（16 位小写十六进制且非全零）。
    /// </summary>
    private static bool IsValidSpanId(string spanId) =>
        spanId.Length == 16
        && spanId.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
        && !spanId.All(c => c == '0');

    /// <summary>
    /// 校验 SDK 标识是否支持（兼容新旧两个版本）。
    /// </summary>
    /// <param name="sdk">SDK 标识字符串。</param>
    /// <returns>有效返回 true；否则返回 false。</returns>
    private static bool IsSupportedSdk(string sdk)
        => string.Equals(sdk, DiagnosticConstants.CurrentSdkName, StringComparison.Ordinal)
           || string.Equals(sdk, DiagnosticConstants.LegacySdkName, StringComparison.Ordinal);

    /// <summary>
    /// 严格读取字符串属性。
    /// </summary>
    private static bool TryReadString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    /// <summary>
    /// 严格读取 32 位整型属性。
    /// </summary>
    private static bool TryReadInt32(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }

    /// <summary>
    /// 宽松读取可选字符串属性。
    /// </summary>
    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// 宽松读取可选 64 位长整型属性。
    /// </summary>
    private static long ReadInt64(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
                ? number
                : 0;

    /// <summary>
    /// 读取 tags 键值字典。
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadTags(JsonElement root)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("tags", out var value) && value.ValueKind == JsonValueKind.Object)
        {
            foreach (var tag in value.EnumerateObject())
            {
                tags[tag.Name] = tag.Value.ValueKind == JsonValueKind.String
                    ? tag.Value.GetString() ?? string.Empty
                    : tag.Value.GetRawText();
            }
        }

        return tags;
    }
}
