using System.Text.Json;

namespace Ability.Core;

/// <summary>
/// 受管资源租约申请体；由技能向上游申请独占或共享访问权。
/// </summary>
/// <param name="Resource">受管资源的 URI 或绝对路径。</param>
/// <param name="Mode">访问模式，如 "Read"、"Write"。</param>
public sealed record LeaseRequestBody(string Resource, string Mode);

/// <summary>
/// 租约批准答复体；返回宿主登记的租约标识。
/// </summary>
/// <param name="LeaseId">宿主分配的唯一租约编号。</param>
public sealed record LeaseGrantedBody(string LeaseId);

/// <summary>
/// 租约拒绝答复体；表示宿主仲裁拒绝了本次租约申请。
/// </summary>
/// <param name="Reason">拒绝的原因说明。</param>
public sealed record LeaseDeniedBody(string Reason);

/// <summary>
/// 租约释放通知体；技能在受管操作结束后主动上报释放已分配的租约。
/// </summary>
/// <param name="LeaseId">待释放的租约编号。</param>
public sealed record LeaseReleaseBody(string LeaseId);

/// <summary>
/// 参数补充申请体；用于在运行时索取缺失的配置、凭据或交互输入。
/// </summary>
/// <param name="Key">参数键名标识。</param>
/// <param name="Hint">面向用户的说明或提示文本。</param>
/// <param name="PayloadType">期望的结构化类型名；若为文本补参则为 null。</param>
/// <param name="Schema">可选的 JSON Schema 数据约束（Draft 2020-12）。</param>
/// <param name="UiDescriptor">可选的纯数据控件建议，不包含任何脚本执行。</param>
public sealed record ParamRequestBody(
    string Key,
    string Hint,
    string? PayloadType = null,
    JsonElement? Schema = null,
    JsonElement? UiDescriptor = null);

/// <summary>
/// 参数补充值交付体；文本值与结构化载荷二者互斥。
/// </summary>
/// <param name="Value">文本补参值；当使用结构化补参时为 null。</param>
/// <param name="PayloadType">结构化载荷的类型名称；当为文本补参时为 null。</param>
/// <param name="Payload">以原始 JSON 表达的结构化补参值；当为文本补参时为 null。</param>
public sealed record ParamValueBody(
    string? Value,
    string? PayloadType = null,
    JsonElement? Payload = null);

/// <summary>
/// 数据源恢复请求体；当外部数据源失效时请求宿主介入刷新或重启。
/// </summary>
/// <param name="SourceKind">数据源种类标识。</param>
/// <param name="SourceRef">数据源的稳定引用标识。</param>
/// <param name="FailureReason">具体的失败原因说明。</param>
/// <param name="HotUpdateSupported">技能是否支持在当前进程内就地重读新数据。</param>
public sealed record SourceRepairBody(
    string SourceKind,
    string SourceRef,
    string FailureReason,
    bool HotUpdateSupported);

/// <summary>
/// 数据源恢复结果答复体；通知技能恢复裁决结果。
/// </summary>
/// <param name="Outcome">恢复处理动作，如 "Reread"、"RestartRequired"、"Denied"、"Cancelled"。</param>
public sealed record SourceRepairResultBody(string Outcome);
