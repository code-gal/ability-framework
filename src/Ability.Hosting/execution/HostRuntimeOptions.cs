namespace Ability.Hosting;

/// <summary>
/// 宿主运行时配置与生命周期时限选项。
/// </summary>
public sealed class HostRuntimeOptions
{
    /// <summary>
    /// 管道等待连接超时时限，缺省为 5 秒。
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 协议握手超时时限（涵盖 Hello 发送与 HelloAccepted 接收），缺省为 5 秒。
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 优雅取消后等待进程自行退出的宽限期，超时将执行强制终止（Kill Process Tree），缺省为 1 秒。
    /// </summary>
    public TimeSpan CancelGracePeriod { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 任务整体最大执行时限；到期后将触发优雅取消流程。为空表示不设上限。
    /// </summary>
    public TimeSpan? ExecutionTimeout { get; init; }

    /// <summary>
    /// 子进程构建准备超时时限，缺省为 30 秒。
    /// </summary>
    public TimeSpan BuildTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 流式结果信用额度窗口大小，限制技能端在途未消费的元素总数，缺省为 4。
    /// </summary>
    public int ResultCreditWindow { get; init; } = 4;

    /// <summary>
    /// 出站控制帧缓冲上限，缺省为 64。
    /// </summary>
    public int MaxPendingControlFrames { get; init; } = 64;

    /// <summary>
    /// 任务专属临时目录根目录；缺省使用系统临时目录下的 "ability-tasks"。
    /// </summary>
    public string? TaskTempRootDirectory { get; init; }

    /// <summary>
    /// 任务结果消费转存回调：在技能进程完全退出后、临时目录删除前执行。
    /// 调用方可在此将需要长期持久化的产物移出目录；回调抛出异常或超时时将保守保留临时目录。
    /// </summary>
    public Func<string, CancellationToken, ValueTask>? TaskTempConsumer { get; init; }

    /// <summary>
    /// 任务结果消费转存回调执行的等待时限，缺省为 30 秒。
    /// </summary>
    public TimeSpan TaskTempConsumerTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 流式结果项消费回调：每当从技能端接收到一个流元素时触发，消费完成后向技能端归还额度。
    /// </summary>
    public Func<StreamResultItem, CancellationToken, ValueTask>? ResultItemCallback { get; init; }

    /// <summary>
    /// 进度通知回调。
    /// </summary>
    public Action<HostProgress>? ProgressCallback { get; init; }

    /// <summary>
    /// 诊断信息输出回调。
    /// </summary>
    public Action<string>? DiagnosticCallback { get; init; }

    /// <summary>
    /// 单个治理请求（租约/补参/恢复）的决断超时时限；若配置且超时，宿主将向技能回复 RequestTimeout 故障。
    /// </summary>
    public TimeSpan? RequestTimeout { get; init; }

    /// <summary>
    /// 租约申请决策回调。
    /// </summary>
    public Func<long, Ability.Core.LeaseRequestBody, CancellationToken, ValueTask<LeaseDecision?>>? LeaseDecisionCallback { get; init; }

    /// <summary>
    /// 参数补充申请决策回调。
    /// </summary>
    public Func<long, Ability.Core.ParamRequestBody, CancellationToken, ValueTask<ParamDecision?>>? ParamDecisionCallback { get; init; }

    /// <summary>
    /// 数据源失效恢复决策回调。
    /// </summary>
    public Func<long, Ability.Core.SourceRepairBody, CancellationToken, ValueTask<SourceRepairDecision?>>? SourceRepairCallback { get; init; }

    /// <summary>
    /// 宿主全局策略覆盖；配置后优先于技能建议策略生效。
    /// </summary>
    public Ability.Core.RunPolicyBody? PolicyOverride { get; init; }

    /// <summary>
    /// 策略动态更新读取通道；宿主后台任务将持续读取并向技能下发 PolicyUpdate。
    /// </summary>
    public System.Threading.Channels.ChannelReader<RunPolicyUpdate>? PolicyUpdates { get; init; }

    /// <summary>
    /// 受管操作状态与快照变更回调。
    /// </summary>
    public Action<OperationSnapshot>? OperationSnapshotCallback { get; init; }

    /// <summary>
    /// 受管操作结构化进度通知回调。
    /// </summary>
    public Action<Ability.Core.ProgressBody>? OperationProgressCallback { get; init; }

    /// <summary>
    /// OTLP Trace 导出 HTTP 端点地址；配置后将在宿主收尾时将收集到的 Span/Log 记录通过 OTLP/HTTP 导出。
    /// </summary>
    public string? TraceExportEndpoint { get; init; }

    /// <summary>
    /// 诊断追踪服务名称，缺省为 "ability-task"。
    /// </summary>
    public string TraceServiceName { get; init; } = "ability-task";

    /// <summary>
    /// 诊断追踪导出超时时限，缺省为 5 秒。
    /// </summary>
    public TimeSpan TraceExportTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 诊断追踪导出附加 HTTP 请求头字典。
    /// </summary>
    public IReadOnlyDictionary<string, string>? TraceHeaders { get; init; }

    /// <summary>
    /// 时间提供者，缺省使用系统时间提供者。
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// 策略动态更新确认等待超时，缺省 5 秒。
    /// </summary>
    public TimeSpan PolicyUpdateTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 未决策略更新表容量上限，缺省 64。
    /// </summary>
    public int MaxPendingPolicyUpdates { get; init; } = 64;

    /// <summary>
    /// 校验配置参数合法性；不合法时立即抛出异常。
    /// </summary>
    internal void Validate()
    {
        if (ConnectTimeout <= TimeSpan.Zero
            || HandshakeTimeout <= TimeSpan.Zero
            || CancelGracePeriod <= TimeSpan.Zero
            || BuildTimeout <= TimeSpan.Zero
            || TaskTempConsumerTimeout <= TimeSpan.Zero
            || (ExecutionTimeout is TimeSpan executionTimeout && (executionTimeout <= TimeSpan.Zero || executionTimeout.TotalMilliseconds > (uint.MaxValue - 1)))
            || (RequestTimeout is TimeSpan requestTimeout && requestTimeout <= TimeSpan.Zero)
            || PolicyUpdateTimeout <= TimeSpan.Zero
            || TraceExportTimeout <= TimeSpan.Zero
            || MaxPendingPolicyUpdates <= 0
            || ResultCreditWindow <= 0
            || ResultCreditWindow > 65536
            || MaxPendingControlFrames <= 0
            || TimeProvider is null)
        {
            throw new ArgumentOutOfRangeException(nameof(HostRuntimeOptions), "配置项包含非法的时限或容量数值。");
        }

        if (TraceExportEndpoint is not null
            && !Uri.TryCreate(TraceExportEndpoint, UriKind.Absolute, out _))
        {
            throw new ArgumentException("TraceExportEndpoint 必须为合法的绝对 URI 地址。", nameof(TraceExportEndpoint));
        }
    }
}
