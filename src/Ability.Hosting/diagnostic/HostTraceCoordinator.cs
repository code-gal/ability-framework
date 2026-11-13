using System.Diagnostics;
using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// 宿主端诊断追踪协调器，负责在任务收尾时收集所有登记调用的 stderr 结构化追踪与日志记录，并异步导出至 OTLP 端点。
/// </summary>
internal static class HostTraceCoordinator
{
    /// <summary>
    /// 等待所有调用的 stderr 读取完成，收集全部结构化追踪与日志记录，并按需导出至 OTLP 端点。
    /// 无论导出成功与否均安全捕获异常，绝不干扰已确立的业务执行结果。
    /// </summary>
    /// <param name="registry">权威调用登记表。</param>
    /// <param name="options">宿主运行时选项。</param>
    /// <param name="hostActivity">可选的宿主根 Activity 实例。</param>
    /// <param name="diagnosticReporter">可选的诊断通道分发委托。</param>
    /// <param name="issueRecorder">可选的问题收集委托。</param>
    /// <returns>异步导出任务。</returns>
    public static async Task CollectAndExportAsync(
        InvocationRegistry registry,
        HostRuntimeOptions options,
        Activity? hostActivity = null,
        Action<string>? diagnosticReporter = null,
        Action<HostCleanupIssue>? issueRecorder = null)
    {
        if (options.TraceExportEndpoint is null)
        {
            return;
        }

        if (!Uri.TryCreate(options.TraceExportEndpoint, UriKind.Absolute, out var endpoint))
        {
            return;
        }

        var reporter = diagnosticReporter ?? options.DiagnosticCallback;

        try
        {
            var records = new List<HostTraceRecord>();
            var drainTimeout = TimeSpan.FromSeconds(3);
            foreach (var record in registry.AllRecords)
            {
                if (record.StandardErrorTask is { } stderrTask)
                {
                    try
                    {
                        using var drainCts = new CancellationTokenSource(drainTimeout);
                        await stderrTask.WaitAsync(drainCts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                records.AddRange(record.TraceCollector.SnapshotRecords());
            }

            var hostSpan = hostActivity is null
                ? null
                : HostTraceSpan.FromHostActivity(
                    hostActivity,
                    HostTraceExport.ToUnixTimeNanoseconds(DateTimeOffset.UtcNow));

            var traces = HostTraceExport.BuildRequest(hostSpan, records, options.TraceServiceName);
            var logs = HostTraceExport.BuildLogsRequest(records, options.TraceServiceName);

            await HostTraceExport.ExportAsync(
                endpoint,
                traces,
                logs,
                options.TraceExportTimeout,
                reporter,
                CancellationToken.None,
                options.TraceHeaders,
                issueRecorder).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var safeDesc = ex is ISafeFaultDescription safe && !string.IsNullOrWhiteSpace(safe.SafeDescription)
                ? safe.SafeDescription
                : ex.GetType().Name;
            issueRecorder?.Invoke(new HostCleanupIssue("TraceExport", ex.GetType().Name, $"[Trace] 诊断导出失败: {safeDesc}"));
            try
            {
                reporter?.Invoke($"[Trace] 诊断导出失败: {ex.GetType().Name}");
            }
            catch
            {
            }
        }
    }
}
