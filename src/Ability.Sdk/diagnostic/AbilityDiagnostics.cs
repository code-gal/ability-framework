using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Ability.Core;

namespace Ability.Sdk;

/// <summary>
/// 技能进程的结构化诊断出口，统一向标准错误输出（stderr）写入单行结构化诊断记录。
/// </summary>
public static class AbilityDiagnostics
{
    /// <summary>
    /// 宿主用来识别结构化诊断行的固定标记。
    /// </summary>
    public const string Marker = DiagnosticConstants.Marker;

    /// <summary>
    /// 当前诊断契约格式版本号。
    /// </summary>
    public const int DiagnosticVersion = DiagnosticConstants.DiagnosticVersion;

    /// <summary>
    /// 实现名称，宿主按该字段识别产生记录的 SDK。
    /// </summary>
    public const string SdkName = DiagnosticConstants.CurrentSdkName;

    /// <summary>
    /// 诊断缓冲队列的默认容量上限。
    /// </summary>
    internal const int DefaultCapacity = 1024;

    /// <summary>
    /// 退出时诊断缓冲队列的默认有限排空时限。
    /// </summary>
    internal static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 单条诊断记录中文本消息字段的最大允许字符长度。
    /// </summary>
    internal const int MaxMessageLength = 4096;

    /// <summary>
    /// 单条诊断记录中类别名称字段的最大允许字符长度。
    /// </summary>
    internal const int MaxCategoryLength = 256;

    /// <summary>
    /// 单条诊断记录中标签值字段的最大允许字符长度。
    /// </summary>
    internal const int MaxTagValueLength = 512;

    /// <summary>
    /// 单条诊断记录中状态描述字段的最大允许字符长度。
    /// </summary>
    internal const int MaxStatusDescriptionLength = 512;

    /// <summary>
    /// 有界诊断行缓冲队列（满载时 DropWrite 淘汰新项，由 itemDropped 回调原子累加统计丢弃）。
    /// </summary>
    private static readonly Channel<string> Lines = Channel.CreateBounded<string>(
        new BoundedChannelOptions(DefaultCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        },
        itemDropped: _ => Interlocked.Increment(ref _dropped));

    /// <summary>
    /// 停止诊断写出循环的取消令牌源。
    /// </summary>
    private static readonly CancellationTokenSource Shutdown = new();

    /// <summary>
    /// 唯一的后台 stderr 诊断行写出循环任务。
    /// </summary>
    private static readonly Task WriterLoop = Task.Run(WriteLoopAsync);

    /// <summary>
    /// 缓冲满载导致的丢弃计数。
    /// </summary>
    private static long _dropped;

    /// <summary>
    /// 诊断出口是否已停止接受新记录的标志位。
    /// </summary>
    private static int _stopped;

    /// <summary>
    /// 获取因缓冲满载而被丢弃的记录总数。
    /// </summary>
    public static long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>
    /// 提取异常的安全摘要说明；非显式实现 <see cref="ISafeFaultDescription"/> 的异常仅保留类型名，防止敏感信息外泄。
    /// </summary>
    /// <param name="exception">待提取摘要的异常对象。</param>
    /// <returns>安全可导出的异常摘要描述。</returns>
    public static string ExceptionSummary(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            ISafeFaultDescription { SafeDescription.Length: > 0 } declared => declared.SafeDescription,
            OperationCanceledException => "操作已取消",
            _ => $"{exception.GetType().Name}: 异常原文未导出"
        };
    }

    /// <summary>
    /// 写入一条结构化日志记录。
    /// </summary>
    /// <param name="level">日志级别名称。</param>
    /// <param name="category">日志记录类别。</param>
    /// <param name="eventId">事件标识编号。</param>
    /// <param name="message">日志文本消息。</param>
    public static void Log(string level, string category, int eventId, string message)
        => Enqueue(writer =>
        {
            writer.WriteString("kind", "log");
            writer.WriteString("level", Limit(level, 32));
            writer.WriteString("category", Limit(category, MaxCategoryLength));
            writer.WriteNumber("eventId", eventId);
            writer.WriteString("message", Limit(message, MaxMessageLength));
            var activity = Activity.Current;
            writer.WriteString("traceId", activity?.TraceId.ToHexString() ?? string.Empty);
            writer.WriteString("spanId", activity?.SpanId.ToHexString() ?? string.Empty);
            writer.WriteNumber("timeUnixNano", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000);
        });

    /// <summary>
    /// 写入一条 Span 开始记录。
    /// </summary>
    /// <param name="activity">已启动的 Activity 实例。</param>
    internal static void SpanStart(Activity activity)
        => Enqueue(writer => WriteSpan(writer, "span_start", activity, endTimeUnixNano: 0));

    /// <summary>
    /// 写入一条 Span 结束记录。
    /// </summary>
    /// <param name="activity">已停止的 Activity 实例。</param>
    internal static void SpanEnd(Activity activity)
        => Enqueue(writer => WriteSpan(
            writer,
            "span_end",
            activity,
            new DateTimeOffset(activity.StartTimeUtc.Add(activity.Duration)).ToUnixTimeNanoseconds()));

    /// <summary>
    /// 停止诊断出口并在指定时限内排空剩余记录。
    /// </summary>
    /// <param name="timeout">排空等待时限；为空时采用默认 2 秒。</param>
    /// <returns>异步排空任务。</returns>
    internal static async Task ShutdownAsync(TimeSpan? timeout = null)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        Shutdown.Cancel();
        try
        {
            await WriterLoop.WaitAsync(timeout ?? DefaultDrainTimeout);
        }
        catch (Exception)
        {
            // 排空超时或写入失败仅记录诊断完整度受损，不中断调用流程
        }
    }

    /// <summary>
    /// 序列化 Span 记录的关键字段到 JSON 写出器。
    /// </summary>
    /// <param name="writer">JSON 写出器。</param>
    /// <param name="kind">记录类别（span_start 或 span_end）。</param>
    /// <param name="activity">Activity 实例。</param>
    /// <param name="endTimeUnixNano">结束时间纳秒戳；为 0 表示尚未结束。</param>
    private static void WriteSpan(Utf8JsonWriter writer, string kind, Activity activity, long endTimeUnixNano)
    {
        writer.WriteString("kind", kind);
        writer.WriteString("name", Limit(activity.DisplayName, 256));
        writer.WriteString("source", Limit(activity.Source.Name, 256));
        writer.WriteString("activityKind", activity.Kind.ToString());
        writer.WriteString("traceId", activity.TraceId.ToHexString());
        writer.WriteString("spanId", activity.SpanId.ToHexString());
        writer.WriteString("parentSpanId", activity.ParentSpanId == default ? string.Empty : activity.ParentSpanId.ToHexString());
        writer.WriteString("traceState", Limit(activity.TraceStateString ?? string.Empty, 512));
        writer.WriteNumber("startTimeUnixNano", activity.StartTimeUtc.ToUnixTimeNanoseconds());
        if (endTimeUnixNano != 0)
        {
            writer.WriteNumber("endTimeUnixNano", endTimeUnixNano);
        }

        writer.WriteString("status", activity.Status.ToString());
        writer.WriteString("statusDescription", Limit(activity.StatusDescription ?? string.Empty, MaxStatusDescriptionLength));
        writer.WriteStartObject("tags");
        foreach (var tag in TraceTagPolicy.Filter(activity))
        {
            writer.WriteString(Limit(tag.Key, 128), Limit(tag.Value, MaxTagValueLength));
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// 截断字符串到指定的最大字符数上限。
    /// </summary>
    /// <param name="value">原始字符串。</param>
    /// <param name="maxLength">最大允许字符数。</param>
    /// <returns>截断后的字符串。</returns>
    private static string Limit(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>
    /// 将生成的诊断行压入缓冲队列；由通道 itemDropped 回调在满载时原子累加丢弃计数，保持单读者契约。
    /// </summary>
    /// <param name="write">写入 JSON 内容的委托。</param>
    private static void Enqueue(Action<Utf8JsonWriter> write)
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        var line = Format(write);
        Lines.Writer.TryWrite(line);
    }

    /// <summary>
    /// 将诊断写入委托格式化为带固定标记的前缀单行 UTF-8 字符串。
    /// </summary>
    /// <param name="write">JSON 写入动作。</param>
    /// <returns>格式化后的诊断行文本。</returns>
    private static string Format(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("diagnosticVersion", DiagnosticVersion);
            writer.WriteString("sdk", SdkName);
            write(writer);
            writer.WriteEndObject();
        }

        return Marker + Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// 后台诊断行写出循环，持续从缓冲队列读取并写出至标准错误流。
    /// </summary>
    /// <returns>写出循环任务。</returns>
    private static async Task WriteLoopAsync()
    {
        try
        {
            while (await Lines.Reader.WaitToReadAsync(Shutdown.Token))
            {
                Drain();
            }
        }
        catch (OperationCanceledException)
        {
            // 收到退出信号，进入退出前排空阶段
        }

        Drain();
    }

    /// <summary>
    /// 同步排空当前缓冲队列中的所有诊断行至标准错误流（单次调用原子输出正文与换行）。
    /// </summary>
    private static void Drain()
    {
        while (Lines.Reader.TryRead(out var line))
        {
            try
            {
                Console.Error.WriteLine(line);
            }
            catch (Exception)
            {
                return;
            }
        }
    }
}

/// <summary>
/// Span 属性标签白名单及脱敏策略。
/// </summary>
internal static class TraceTagPolicy
{
    /// <summary>
    /// 允许跨进程导出的标签键白名单集合。
    /// </summary>
    private static readonly string[] AllowedKeys =
    [
        "http.request.method",
        "http.response.status_code",
        "server.address",
        "url.path",
        "operation.key",
        "attempt"
    ];

    /// <summary>
    /// 根据白名单过滤并脱敏 Activity 中的标签对象。
    /// </summary>
    /// <param name="activity">Activity 实例。</param>
    /// <returns>脱敏后的键值对序列。</returns>
    internal static IEnumerable<KeyValuePair<string, string>> Filter(Activity activity)
    {
        foreach (var tag in activity.TagObjects)
        {
            if (!AllowedKeys.Contains(tag.Key, StringComparer.Ordinal))
            {
                continue;
            }

            var value = tag.Value switch
            {
                null => string.Empty,
                string text => tag.Key == "url.path" ? Sanitize(text) : text,
                _ => Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
            };
            yield return new KeyValuePair<string, string>(tag.Key, value);
        }

        if (!activity.TagObjects.Any(tag => string.Equals(tag.Key, "url.path", StringComparison.Ordinal))
            && activity.TagObjects.FirstOrDefault(tag => string.Equals(tag.Key, "url.full", StringComparison.Ordinal)).Value is string full)
        {
            yield return new KeyValuePair<string, string>("url.path", Sanitize(full));
        }
    }

    /// <summary>
    /// 脱敏 URL 字符串，仅保留 Scheme、Host 与 AbsolutePath，剔除查询串、哈希与认证信息。
    /// 解析失败时剥离可能存在的查询、哈希与用户信息凭据。
    /// </summary>
    /// <param name="url">原始 URL 字符串。</param>
    /// <returns>脱敏后的 URL 路径。</returns>
    private static string Sanitize(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var clean = url;
            var separator = clean.IndexOfAny(['?', '#']);
            if (separator >= 0)
            {
                clean = clean[..separator];
            }

            var atIndex = clean.IndexOf('@');
            var schemeIndex = clean.IndexOf("://", StringComparison.Ordinal);
            if (atIndex >= 0 && schemeIndex >= 0 && atIndex > schemeIndex + 3)
            {
                clean = string.Concat(clean.AsSpan(0, schemeIndex + 3), clean.AsSpan(atIndex + 1));
            }

            return clean;
        }

        return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
    }
}

/// <summary>
/// Unix 时间戳换算扩展工具类。
/// </summary>
internal static class UnixTimeExtensions
{
    /// <summary>
    /// 将 DateTimeOffset 换算为 Unix 纳秒时间戳。
    /// </summary>
    /// <param name="value">时间值。</param>
    /// <returns>Unix 纳秒时间戳。</returns>
    internal static long ToUnixTimeNanoseconds(this DateTimeOffset value)
        => value.ToUnixTimeMilliseconds() * 1_000_000 + (value.Ticks % TimeSpan.TicksPerMillisecond) * 100;

    /// <summary>
    /// 将 DateTime 换算为 Unix 纳秒时间戳（按 UTC 解释）。
    /// </summary>
    /// <param name="value">时间值。</param>
    /// <returns>Unix 纳秒时间戳。</returns>
    internal static long ToUnixTimeNanoseconds(this DateTime value)
        => new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeNanoseconds();
}
