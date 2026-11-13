using System.Collections.ObjectModel;

namespace Ability.Hosting;

/// <summary>
/// 最终收尾报告，表达单次调用结束时所有资源的实际最终处置事实。
/// </summary>
public sealed record HostCleanupReport
{
    /// <summary>
    /// 整树进程退出是否确认。
    /// </summary>
    public bool ProcessExitConfirmed { get; init; }

    /// <summary>
    /// 租约与治理归属是否完成结算。
    /// </summary>
    public bool GovernanceResourcesReclaimed { get; init; }

    /// <summary>
    /// 诊断工作（stderr 采集、Trace 导出与派发）是否实际结束。
    /// </summary>
    public bool DiagnosticsCompleted { get; init; }

    /// <summary>
    /// 其余受管工作及其派生清理是否实际结束。
    /// </summary>
    public bool DeferredWorkCompleted { get; init; }

    /// <summary>
    /// 资源释放与目录处置是否完全满足既定策略条件。
    /// </summary>
    public bool CleanupCompleted { get; init; }

    /// <summary>
    /// 专属任务临时目录最终生命周期报告。
    /// </summary>
    public TaskTempReport? TaskTemp { get; init; }

    /// <summary>
    /// 内部维护的只读收尾问题集合。
    /// </summary>
    private readonly IReadOnlyList<HostCleanupIssue> _issues = Array.AsReadOnly(Array.Empty<HostCleanupIssue>());

    /// <summary>
    /// 收尾过程中记录的问题列表。
    /// </summary>
    public IReadOnlyList<HostCleanupIssue> Issues
    {
        get => _issues;
        init => _issues = Array.AsReadOnly(value?.ToArray() ?? Array.Empty<HostCleanupIssue>());
    }
}

