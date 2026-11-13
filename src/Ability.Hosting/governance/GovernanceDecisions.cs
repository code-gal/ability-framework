using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// 宿主对技能租约申请的决策结果。
/// 回调返回 null 表示当前暂不决断并继续等待；若拒绝，Reason 将作为拒绝原因说明回传给技能。
/// </summary>
/// <param name="Granted">是否批准租约申请。</param>
/// <param name="LeaseId">本次授予的租约唯一标识；同一任务治理会话内不得复用，也不可用作可重复资源名称；若为空由宿主自动生成。</param>
/// <param name="Reason">拒绝申请的原因说明；若为空使用默认拒绝原因。</param>
public sealed record LeaseDecision(bool Granted, string? LeaseId = null, string? Reason = null);

/// <summary>
/// 宿主对技能参数补充申请的决策结果。
/// 文本与结构化结果形态互斥：文本参数仅提供 <see cref="Value"/>，结构化参数提供 <see cref="PayloadType"/> 与 <see cref="Payload"/>。
/// </summary>
/// <param name="Value">文本参数值。</param>
/// <param name="PayloadType">结构化载荷类型名称。</param>
/// <param name="Payload">结构化 JSON 载荷元素。</param>
public sealed record ParamDecision(string? Value, string? PayloadType = null, JsonElement? Payload = null)
{
    /// <summary>
    /// 从强类型业务模型和源生成 JSON 类型元数据构造结构化参数决策。
    /// </summary>
    /// <typeparam name="T">业务模型类型。</typeparam>
    /// <param name="value">业务模型实例。</param>
    /// <param name="jsonTypeInfo">源生成 JSON 类型元数据。</param>
    /// <returns>构造完成的结构化参数决策实例。</returns>
    public static ParamDecision FromModel<T>(T value, JsonTypeInfo<T> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        return new(null, jsonTypeInfo.Type.Name, JsonSerializer.SerializeToElement(value, jsonTypeInfo));
    }
}

/// <summary>
/// 宿主对技能数据源恢复申请的决策结果。
/// <see cref="Outcome"/> 取值必须为 Reread、RestartRequired、Denied、Cancelled 之一。
/// </summary>
/// <param name="Outcome">仲裁恢复动作结论。</param>
public sealed record SourceRepairDecision(string Outcome);

/// <summary>
/// 外部向宿主提交的针对指定受管操作的动态策略更新请求。
/// 宿主校验后将其编组为 PolicyUpdate 协议信封发送给技能。
/// </summary>
/// <param name="OperationKey">目标受管操作的唯一标识键。</param>
/// <param name="Version">新策略单调递增的版本号。</param>
/// <param name="Source">更新发起方来源说明。</param>
/// <param name="Policy">更新生效的运行策略协议体。</param>
public sealed record RunPolicyUpdate(
    string OperationKey,
    long Version,
    string Source,
    RunPolicyBody Policy);
