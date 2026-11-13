using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;

namespace Ability.Hosting;

/// <summary>
/// 将收集到的结构化诊断记录转换为标准 OTLP（HTTP/protobuf）并发送至观测端。
/// 完全基于 .NET BCL 实现，不依赖任何第三方 OpenTelemetry 客户端类库。
/// 导出在进程回收后执行，导出异常与超时仅记录诊断日志，不影响已确立的业务执行结果。
/// </summary>
internal static class HostTraceExport
{
    /// <summary>
    /// 宿主根 Span 来源标识名称。
    /// </summary>
    internal const string HostSourceName = "Ability.Host";

    /// <summary>
    /// 缺失结束记录时补写的明确中断描述。
    /// </summary>
    internal const string InterruptedDescription = "宿主未收到 span 结束记录，进程已被强制回收";

    /// <summary>
    /// 将 DateTimeOffset 转换为 Unix 纳秒时间戳。
    /// </summary>
    /// <param name="value">时间值。</param>
    /// <returns>Unix 纳秒值。</returns>
    internal static long ToUnixTimeNanoseconds(DateTimeOffset value)
        => value.ToUnixTimeMilliseconds() * 1_000_000 + (value.Ticks % TimeSpan.TicksPerMillisecond) * 100;

    /// <summary>
    /// 把单次调用的结构化记录整理成 OTLP 追踪导出请求字节体（ExportTraceServiceRequest）。
    /// </summary>
    /// <param name="hostSpan">宿主生成的根 Span；若无则为 null。</param>
    /// <param name="records">所有子调用收集到的结构化记录。</param>
    /// <param name="serviceName">Resource 中的 service.name 属性。</param>
    /// <returns>符合 protobuf 编码规范的请求字节流。</returns>
    internal static byte[] BuildRequest(
        HostTraceSpan? hostSpan,
        IReadOnlyList<HostTraceRecord> records,
        string serviceName)
    {
        var spans = new List<HostTraceSpan>();
        if (hostSpan is not null)
        {
            spans.Add(hostSpan);
        }

        var openSpans = new Dictionary<string, HostTraceRecord>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            switch (record.Kind)
            {
                case "span_start":
                    openSpans[record.SpanId] = record;
                    break;
                case "span_end":
                    if (openSpans.Remove(record.SpanId, out var start))
                    {
                        spans.Add(HostTraceSpan.From(start, record));
                    }
                    else
                    {
                        spans.Add(HostTraceSpan.From(record, record));
                    }
                    break;
            }
        }

        foreach (var pair in openSpans)
        {
            spans.Add(HostTraceSpan.Interrupted(pair.Value));
        }

        return EncodeSpans(spans, serviceName);
    }

    /// <summary>
    /// 把日志记录整理成 OTLP 日志导出请求体（ExportLogsServiceRequest）。
    /// </summary>
    /// <param name="records">所有调用收集到的结构化记录。</param>
    /// <param name="serviceName">写入 Resource 的 service.name。</param>
    /// <returns>符合 protobuf 编码规范的请求字节流。</returns>
    internal static byte[] BuildLogsRequest(IReadOnlyList<HostTraceRecord> records, string serviceName)
    {
        var logs = records.Where(r => r.Kind == "log").ToArray();
        var byCategory = logs
            .GroupBy(r => string.IsNullOrEmpty(r.Category) ? "Ability" : r.Category, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToArray();

        var request = new ProtoWriter();
        request.WriteMessage(1, resourceLogs =>
        {
            resourceLogs.WriteMessage(1, resource =>
            {
                resource.WriteMessage(1, attribute =>
                {
                    attribute.WriteString(1, "service.name");
                    attribute.WriteMessage(2, val => val.WriteString(1, serviceName));
                });
            });

            foreach (var group in byCategory)
            {
                resourceLogs.WriteMessage(2, scopeLogs =>
                {
                    scopeLogs.WriteMessage(1, scope => scope.WriteString(1, group.Key));
                    foreach (var log in group)
                    {
                        scopeLogs.WriteMessage(2, writer => EncodeLog(writer, log));
                    }
                });
            }
        });

        return request.ToArray();
    }

    /// <summary>
    /// 按 OTLP LogRecord 字段号编码一条日志记录。
    /// </summary>
    private static void EncodeLog(ProtoWriter writer, HostTraceRecord log)
    {
        var time = log.TimeUnixNano == 0 ? log.StartTimeUnixNano : log.TimeUnixNano;
        writer.WriteFixed64(1, (ulong)time);
        writer.WriteVarint(2, SeverityNumber(log.Level));
        writer.WriteString(3, log.Level.Length == 0 ? "Information" : log.Level);
        writer.WriteMessage(5, body => body.WriteString(1, log.Message));
        writer.WriteMessage(6, attribute =>
        {
            attribute.WriteString(1, "log.category");
            attribute.WriteMessage(2, val => val.WriteString(1, log.Category));
        });
        writer.WriteMessage(6, attribute =>
        {
            attribute.WriteString(1, "log.event_id");
            attribute.WriteMessage(2, val => val.WriteNumber(3, log.EventId));
        });
        writer.WriteBytes(9, HexToBytes(log.TraceId));
        writer.WriteBytes(10, HexToBytes(log.SpanId));
    }

    /// <summary>
    /// 映射日志级别至 OTLP SeverityNumber。
    /// </summary>
    private static ulong SeverityNumber(string level)
        => level switch
        {
            "Trace" => 1,
            "Debug" => 5,
            "Information" => 9,
            "Warning" => 13,
            "Error" => 17,
            "Critical" => 21,
            _ => 0
        };

    /// <summary>
    /// 发送 OTLP 追踪与日志导出请求。
    /// 任何网络失败、超时或非成功状态码均安全记录诊断，绝不抛出异常破坏主流程。
    /// </summary>
    /// <param name="endpoint">OTLP traces 接收端点 URI。</param>
    /// <param name="traces">已编码的 traces protobuf 字节流。</param>
    /// <param name="logs">已编码的 logs protobuf 字节流。</param>
    /// <param name="timeout">单次 HTTP 请求超时时限。</param>
    /// <param name="diagnostic">诊断输出回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="headers">随请求发送的附加 HTTP 标头字典。</param>
    /// <param name="issueRecorder">可选的问题收集委托。</param>
    internal static async Task ExportAsync(
        Uri endpoint,
        byte[] traces,
        byte[] logs,
        TimeSpan timeout,
        Action<string>? diagnostic,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers = null,
        Action<HostCleanupIssue>? issueRecorder = null)
    {
        await PostAsync(endpoint, traces, timeout, diagnostic, cancellationToken, headers, issueRecorder).ConfigureAwait(false);
        var logsEndpoint = LogsEndpoint(endpoint);
        if (logsEndpoint is not null)
        {
            await PostAsync(logsEndpoint, logs, timeout, diagnostic, cancellationToken, headers, issueRecorder).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 根据 traces 端点推导 logs 端点（将 /v1/traces 替换为 /v1/logs）。
    /// </summary>
    private static Uri? LogsEndpoint(Uri tracesEndpoint)
    {
        const string tracesSuffix = "/v1/traces";
        var text = tracesEndpoint.ToString();
        return text.EndsWith(tracesSuffix, StringComparison.OrdinalIgnoreCase)
            ? new Uri(text[..^tracesSuffix.Length] + "/v1/logs")
            : null;
    }

    /// <summary>
    /// 发送单次 HTTP POST 请求。
    /// </summary>
    private static async Task PostAsync(
        Uri endpoint,
        byte[] request,
        TimeSpan timeout,
        Action<string>? diagnostic,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers,
        Action<HostCleanupIssue>? issueRecorder = null)
    {
        try
        {
            using var client = new HttpClient { Timeout = timeout };
            using var content = new ByteArrayContent(request);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };

            if (headers is not null)
            {
                foreach (var header in headers)
                {
                    message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var msg = $"OTLP 导出被拒绝：{endpoint.AbsolutePath} {(int)response.StatusCode} {response.ReasonPhrase}";
                diagnostic?.Invoke(msg);
                issueRecorder?.Invoke(new HostCleanupIssue("TraceExportHttp", "HttpRequestRejected", msg));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            var msg = $"OTLP 导出失败：{endpoint.AbsolutePath} {ex.GetType().Name}";
            diagnostic?.Invoke(msg);
            issueRecorder?.Invoke(new HostCleanupIssue("TraceExportHttp", ex.GetType().Name, msg));
        }
    }

    /// <summary>
    /// 把 Span 列表编码为 ExportTraceServiceRequest protobuf 字节流。
    /// </summary>
    private static byte[] EncodeSpans(IReadOnlyList<HostTraceSpan> spans, string serviceName)
    {
        var bySource = spans
            .GroupBy(s => string.IsNullOrEmpty(s.Source) ? HostSourceName : s.Source, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToArray();

        var request = new ProtoWriter();
        request.WriteMessage(1, resourceSpans =>
        {
            resourceSpans.WriteMessage(1, resource =>
            {
                resource.WriteMessage(1, attribute =>
                {
                    attribute.WriteString(1, "service.name");
                    attribute.WriteMessage(2, val => val.WriteString(1, serviceName));
                });
            });

            foreach (var group in bySource)
            {
                resourceSpans.WriteMessage(2, scopeSpans =>
                {
                    scopeSpans.WriteMessage(1, scope => scope.WriteString(1, group.Key));
                    foreach (var span in group)
                    {
                        scopeSpans.WriteMessage(2, writer => EncodeSpan(writer, span));
                    }
                });
            }
        });

        return request.ToArray();
    }

    /// <summary>
    /// 编码单个 Span 结构体。
    /// </summary>
    private static void EncodeSpan(ProtoWriter writer, HostTraceSpan span)
    {
        writer.WriteBytes(1, HexToBytes(span.TraceId));
        writer.WriteBytes(2, HexToBytes(span.SpanId));
        writer.WriteString(3, span.TraceState);
        writer.WriteBytes(4, HexToBytes(span.ParentSpanId));
        writer.WriteString(5, span.Name);
        writer.WriteVarint(6, (ulong)span.Kind);
        writer.WriteFixed64(7, (ulong)span.StartTimeUnixNano);
        writer.WriteFixed64(8, (ulong)span.EndTimeUnixNano);
        foreach (var tag in span.Tags)
        {
            writer.WriteMessage(9, attribute =>
            {
                attribute.WriteString(1, tag.Key);
                attribute.WriteMessage(2, val => val.WriteString(1, tag.Value));
            });
        }

        writer.WriteMessage(15, status =>
        {
            status.WriteString(2, span.StatusDescription);
            status.WriteVarint(3, span.Status switch
            {
                "Ok" => 1,
                "Error" => 2,
                _ => 0
            });
        });
    }

    /// <summary>
    /// 将 16 进制字符串转换为字节数组。
    /// </summary>
    private static byte[] HexToBytes(string hex)
    {
        if (hex.Length == 0 || hex.Length % 2 != 0 || !hex.All(char.IsAsciiHexDigit))
        {
            return [];
        }

        var bytes = new byte[hex.Length / 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = Convert.ToByte(hex.Substring(index * 2, 2), 16);
        }

        return bytes;
    }
}

/// <summary>
/// 一条准备导出的 Span 模型。
/// </summary>
internal sealed class HostTraceSpan
{
    /// <summary>Span 名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Span 来源组件名称。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>W3C Trace ID 标识。</summary>
    public string TraceId { get; init; } = string.Empty;

    /// <summary>当前 Span ID 标识。</summary>
    public string SpanId { get; init; } = string.Empty;

    /// <summary>父级 Span ID 标识。</summary>
    public string ParentSpanId { get; init; } = string.Empty;

    /// <summary>W3C TraceState 状态串。</summary>
    public string TraceState { get; init; } = string.Empty;

    /// <summary>开始时间 Unix 纳秒时间戳。</summary>
    public long StartTimeUnixNano { get; init; }

    /// <summary>结束时间 Unix 纳秒时间戳。</summary>
    public long EndTimeUnixNano { get; set; }

    /// <summary>Span 状态（"Ok"、"Error"、"Unset"）。</summary>
    public string Status { get; set; } = "Unset";

    /// <summary>状态描述信息。</summary>
    public string StatusDescription { get; set; } = string.Empty;

    /// <summary>OTLP SpanKind 枚举整型数值。</summary>
    public int Kind { get; init; }

    /// <summary>诊断标签键值字典。</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// 从宿主根 Activity 构造导出 Span。
    /// </summary>
    /// <param name="activity">根 Activity 实例。</param>
    /// <param name="endTimeUnixNano">导出时刻纳秒时间戳。</param>
    /// <returns>宿主根 Span 实例。</returns>
    internal static HostTraceSpan FromHostActivity(Activity activity, long endTimeUnixNano)
        => new()
        {
            Name = activity.DisplayName,
            Source = HostTraceExport.HostSourceName,
            TraceId = activity.TraceId.ToHexString(),
            SpanId = activity.SpanId.ToHexString(),
            ParentSpanId = activity.ParentSpanId == default ? string.Empty : activity.ParentSpanId.ToHexString(),
            TraceState = activity.TraceStateString ?? string.Empty,
            StartTimeUnixNano = HostTraceExport.ToUnixTimeNanoseconds(new DateTimeOffset(activity.StartTimeUtc, TimeSpan.Zero)),
            EndTimeUnixNano = endTimeUnixNano,
            Status = activity.Status == ActivityStatusCode.Error ? "Error" : activity.Status == ActivityStatusCode.Ok ? "Ok" : "Unset",
            StatusDescription = activity.StatusDescription ?? string.Empty,
            Kind = MapActivityKind(activity.Kind.ToString())
        };

    /// <summary>
    /// 根据配对的开始与结束记录合成完整 Span。
    /// </summary>
    /// <param name="start">Span 开始记录。</param>
    /// <param name="end">Span 结束记录。</param>
    /// <returns>合成后的完整 Span 实例。</returns>
    internal static HostTraceSpan From(HostTraceRecord start, HostTraceRecord end)
        => new()
        {
            Name = start.Name.Length > 0 ? start.Name : end.Name,
            Source = start.Source,
            TraceId = start.TraceId,
            SpanId = start.SpanId,
            ParentSpanId = start.ParentSpanId.Length > 0 ? start.ParentSpanId : end.ParentSpanId,
            TraceState = start.TraceState.Length > 0 ? start.TraceState : end.TraceState,
            StartTimeUnixNano = start.StartTimeUnixNano,
            EndTimeUnixNano = end.EndTimeUnixNano == 0 ? end.StartTimeUnixNano : end.EndTimeUnixNano,
            Status = end.Status,
            StatusDescription = end.StatusDescription,
            Kind = MapActivityKind(start.ActivityKind),
            Tags = end.Tags.Count > 0 ? end.Tags : start.Tags
        };

    /// <summary>
    /// 为缺少结束记录的中断 Span 构造带有 Error 状态的兜底 Span。
    /// </summary>
    /// <param name="start">Span 开始记录。</param>
    /// <returns>标记为中断的 Span 实例。</returns>
    internal static HostTraceSpan Interrupted(HostTraceRecord start)
    {
        var span = From(start, start);
        span.EndTimeUnixNano = start.StartTimeUnixNano;
        span.Status = "Error";
        span.StatusDescription = HostTraceExport.InterruptedDescription;
        return span;
    }

    /// <summary>
    /// 将 ActivityKind 字符串映射为 OTLP SpanKind 枚举整型值。
    /// </summary>
    private static int MapActivityKind(string activityKind)
        => activityKind switch
        {
            "Internal" => 1,
            "Server" => 2,
            "Client" => 3,
            "Producer" => 4,
            "Consumer" => 5,
            _ => 0
        };
}

/// <summary>
/// 纯 BCL 实现的轻量 Protobuf 协议流编码写入器。
/// </summary>
internal sealed class ProtoWriter
{
    /// <summary>
    /// 内部内存输出流。
    /// </summary>
    private readonly MemoryStream _stream = new();

    /// <summary>
    /// 写入 64 位无符号 Varint 字段。
    /// </summary>
    /// <param name="field">字段编号。</param>
    /// <param name="value">字段数值。</param>
    internal void WriteVarint(int field, ulong value)
    {
        WriteTag(field, 0);
        WriteRawVarint(value);
    }

    /// <summary>
    /// 写入整数数值字段。
    /// </summary>
    /// <param name="field">字段编号。</param>
    /// <param name="value">数值。</param>
    internal void WriteNumber(int field, long value) => WriteVarint(field, unchecked((ulong)value));

    /// <summary>
    /// 写入 UTF-8 字符串字段。
    /// </summary>
    /// <param name="field">字段编号。</param>
    /// <param name="value">字符串内容。</param>
    internal void WriteString(int field, string value)
        => WriteBytes(field, Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// 写入字节数组字段（长度分隔）。
    /// </summary>
    /// <param name="field">字段编号。</param>
    /// <param name="value">字节内容。</param>
    internal void WriteBytes(int field, byte[] value)
    {
        if (value.Length == 0)
        {
            return;
        }

        WriteTag(field, 2);
        WriteRawVarint((ulong)value.Length);
        _stream.Write(value);
    }

    /// <summary>
    /// 写入 64 位小端定长字段（Fixed64）。
    /// </summary>
    /// <param name="field">字段编号。</param>
    /// <param name="value">64 位整型数值。</param>
    internal void WriteFixed64(int field, ulong value)
    {
        if (value == 0)
        {
            return;
        }

        WriteTag(field, 1);
        Span<byte> buffer = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        _stream.Write(buffer);
    }

    /// <summary>
    /// 写入嵌套子消息结构（长度分隔）。
    /// </summary>
    /// <param name="field">字段编号。</param>
    /// <param name="write">针对子写入器的写出委托。</param>
    internal void WriteMessage(int field, Action<ProtoWriter> write)
    {
        var nested = new ProtoWriter();
        write(nested);
        WriteBytes(field, nested.ToArray());
    }

    /// <summary>
    /// 获取当前写出的完整二进制数组。
    /// </summary>
    /// <returns>编码后的 Protobuf 字节数组。</returns>
    internal byte[] ToArray() => _stream.ToArray();

    /// <summary>
    /// 写入字段标记（字段编号与传输类型组合）。
    /// </summary>
    private void WriteTag(int field, int wireType) => WriteRawVarint((ulong)((field << 3) | wireType));

    /// <summary>
    /// 编码原始 7 位 Varint 字节。
    /// </summary>
    private void WriteRawVarint(ulong value)
    {
        while (value >= 0x80)
        {
            _stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        _stream.WriteByte((byte)value);
    }
}
