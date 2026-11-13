using System.Text.Json;

namespace Ability.Hosting;

/// <summary>
/// 宿主应用层进度更新模型。
/// </summary>
/// <param name="OperationKey">操作标识。</param>
/// <param name="Sequence">递增进度序号。</param>
/// <param name="Percent">百分比进度（0.0 至 1.0）。</param>
/// <param name="Message">进度描述文本。</param>
/// <param name="Data">附加结构化数据。</param>
public sealed record HostProgress(
    string OperationKey,
    long Sequence,
    double? Percent,
    string? Message,
    JsonElement? Data);
