using System.Collections.ObjectModel;
using System.Text.Json;

namespace Ability.Hosting;

/// <summary>
/// 宿主执行 Ability 任务的完整终态结果、资源清理报告与观测诊断。
/// </summary>
public sealed record AbilityRunResult
{
    /// <summary>
    /// 结果交付条件是否满足。
    /// 宿主裁决为正常、接受正常业务终态（SubmitResult 或 ResultClose）、根进程实际退出码为 0、
    /// 进程整树退出与治理资源回收均已确认，且约定的结果消费成功。
    /// </summary>
    public bool Success =>
        (Outcome is "SubmitResult" or "ResultClose")
        && (BusinessOutcome is "SubmitResult" or "ResultClose")
        && ProcessExitConfirmed
        && GovernanceResourcesReclaimed
        && ProcessExitCode == 0
        && (TaskTemp is null || TaskTemp.ConsumptionStatus is not ("Failed" or "TimedOut"));

    /// <summary>
    /// 已接受的技能终态（SubmitResult、ResultClose 或 SubmitFault）；尚未接受终态时为 null。
    /// </summary>
    public string? BusinessOutcome { get; init; }

    /// <summary>
    /// 确认的根进程退出码；未启动或退出未知时为 null。
    /// </summary>
    public int? ProcessExitCode { get; init; }

    /// <summary>
    /// 整树进程退出是否确认。
    /// </summary>
    public bool ProcessExitConfirmed { get; init; } = true;

    /// <summary>
    /// 租约与治理归属是否完成结算。
    /// </summary>
    public bool GovernanceResourcesReclaimed { get; init; } = true;

    /// <summary>
    /// 诊断工作是否实际结束。
    /// </summary>
    public bool DiagnosticsCompleted { get; init; } = true;

    /// <summary>
    /// 其余受管工作是否实际结束。
    /// </summary>
    public bool DeferredWorkCompleted { get; init; } = true;

    /// <summary>
    /// 资源释放与目录处置是否满足第一节第 3 小节条件。
    /// </summary>
    public bool CleanupCompleted { get; init; } = true;

    /// <summary>
    /// 本次调用的使用结束观察句柄。
    /// </summary>
    public required HostRunLifetime Lifetime { get; init; }

    /// <summary>
    /// 内部维护的只读收尾问题集合。
    /// </summary>
    private readonly IReadOnlyList<HostCleanupIssue> _issues = Array.AsReadOnly(Array.Empty<HostCleanupIssue>());

    /// <summary>
    /// 返回时已知的收尾问题列表。
    /// </summary>
    public IReadOnlyList<HostCleanupIssue> Issues
    {
        get => _issues;
        init => _issues = Array.AsReadOnly(value?.ToArray() ?? Array.Empty<HostCleanupIssue>());
    }

    /// <summary>
    /// 任务收束终态标识（如 "SubmitResult"、"ResultClose"、"Fault"、"Cancelled"、"ConnectTimeout"、"HandshakeTimeout"、"ExecutionTimeout"、"ProcessExitBeforeConnect"、"ProcessCrash"、"Disconnected"、"ProtocolError"、"ConsumerFault" 等）。
    /// </summary>
    public string Outcome { get; init; } = string.Empty;

    /// <summary>
    /// 一元任务输出载荷类型名（一元任务成功时有值）。
    /// </summary>
    public string? OutputTypeName { get; init; }

    /// <summary>
    /// 一元任务输出原始 JSON 载荷（一元任务成功时有值）。
    /// </summary>
    public JsonElement? OutputJson { get; init; }

    /// <summary>
    /// 流式任务实际接收到的流元素项总数。
    /// </summary>
    public int StreamItemCount { get; init; }

    /// <summary>
    /// 故障种类（终态为 Fault 时有值）。
    /// </summary>
    public string? FaultKind { get; init; }

    /// <summary>
    /// 故障详细说明文本（终态为 Fault 时有值）。
    /// </summary>
    public string? FaultMessage { get; init; }

    /// <summary>
    /// 专属任务临时目录的收尾报告。
    /// </summary>
    public TaskTempReport? TaskTemp { get; init; }

    /// <summary>
    /// 子进程 stderr 输出摘要及审计诊断。
    /// </summary>
    public string Diagnostics { get; init; } = string.Empty;

    /// <summary>
    /// 内部维护的只读通信消息类型列表。
    /// </summary>
    private readonly IReadOnlyList<string> _observedMessageTypes = Array.AsReadOnly(Array.Empty<string>());

    /// <summary>
    /// 宿主端按序观察到的通信消息类型列表，用于审计。
    /// </summary>
    public IReadOnlyList<string> ObservedMessageTypes
    {
        get => _observedMessageTypes;
        init => _observedMessageTypes = Array.AsReadOnly(value?.ToArray() ?? Array.Empty<string>());
    }

    /// <summary>
    /// 观察到的通信消息列表是否超过上限而发生了截断。
    /// </summary>
    public bool ObservedMessagesTruncated { get; init; }

    /// <summary>
    /// 内部维护的只读受管操作快照列表。
    /// </summary>
    private readonly IReadOnlyList<OperationSnapshot> _operations = Array.AsReadOnly(Array.Empty<OperationSnapshot>());

    /// <summary>
    /// 任务生命周期中由技能上报并由宿主持续维护的所有受管操作最终快照。
    /// </summary>
    public IReadOnlyList<OperationSnapshot> Operations
    {
        get => _operations;
        init => _operations = Array.AsReadOnly(value?.ToArray() ?? Array.Empty<OperationSnapshot>());
    }
}
