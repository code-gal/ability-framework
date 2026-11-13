using System.Diagnostics;

namespace Ability.Sdk;

/// <summary>
/// 技能侧的原生活动追踪管理门面，负责监听并透传 W3C TraceContext 及记录 Span 生命周期。
/// </summary>
public static class AbilityTracing
{
    /// <summary>
    /// 当前 SDK 的默认 ActivitySource 名称。
    /// </summary>
    public const string SourceName = "AbilityFramework.Sdk";

    /// <summary>
    /// 兼容旧版的 ActivitySource 名称。
    /// </summary>
    public const string LegacySourceName = "SkillIpcMvp.Sdk";

    /// <summary>
    /// 支持采集的活动源名称白名单数组。
    /// </summary>
    internal static readonly string[] SupportedSources = [SourceName, LegacySourceName, "System.Net.Http"];

    /// <summary>
    /// SDK 原生活动源实例。
    /// </summary>
    private static readonly ActivitySource Source = new(SourceName, "3.0");

    /// <summary>
    /// 启动与停止操作的同步互斥锁。
    /// </summary>
    private static readonly object Gate = new();

    /// <summary>
    /// 全局活动监听器。
    /// </summary>
    private static ActivityListener? _listener;

    /// <summary>
    /// 当前任务入口对应的根 Activity 实例。
    /// </summary>
    private static Activity? _entryActivity;

    /// <summary>
    /// 注册活动监听器并用宿主传入的追踪上下文启动入口 Span。
    /// </summary>
    /// <param name="traceParent">W3C traceparent 头部字符串。</param>
    /// <param name="traceState">W3C tracestate 头部字符串。</param>
    /// <param name="spanName">入口 Span 的展示名称。</param>
    internal static void Start(string? traceParent, string? traceState, string spanName)
    {
        lock (Gate)
        {
            if (_listener is null)
            {
                _listener = new ActivityListener
                {
                    ShouldListenTo = source => SupportedSources.Contains(source.Name, StringComparer.Ordinal),
                    Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                    ActivityStarted = AbilityDiagnostics.SpanStart,
                    ActivityStopped = AbilityDiagnostics.SpanEnd
                };
                ActivitySource.AddActivityListener(_listener);
            }

            if (_entryActivity is not null)
            {
                return;
            }

            var hasParent = !string.IsNullOrWhiteSpace(traceParent)
                && ActivityContext.TryParse(traceParent, traceState, out var parent);

            _entryActivity = Source.StartActivity(
                spanName,
                ActivityKind.Server,
                hasParent ? parent : default);
        }
    }

    /// <summary>
    /// 获取当前活动环境下的 W3C 格式 TraceParent 标识字符串。
    /// </summary>
    internal static string? CurrentTraceParent => Activity.Current?.Id;

    /// <summary>
    /// 获取当前活动环境下的 W3C TraceState 状态字符串。
    /// </summary>
    internal static string? CurrentTraceState => Activity.Current?.TraceStateString;

    /// <summary>
    /// 停止入口 Span 并释放监听器资源。
    /// </summary>
    /// <param name="succeeded">调用是否执行成功。</param>
    /// <param name="failureDescription">失败时的安全错误摘要描述。</param>
    internal static void Stop(bool succeeded, string? failureDescription)
    {
        lock (Gate)
        {
            if (_entryActivity is not null)
            {
                if (!succeeded)
                {
                    _entryActivity.SetStatus(ActivityStatusCode.Error, failureDescription);
                }

                _entryActivity.Stop();
                _entryActivity = null;
            }

            _listener?.Dispose();
            _listener = null;
        }
    }
}
