using System.Text.Json;

namespace Ability.Hosting;

/// <summary>
/// 宿主逐项接收并转交给调用方的流元素载荷，不包含业务强类型定义。
/// </summary>
/// <param name="PayloadType">流元素类型名称。</param>
/// <param name="Payload">流元素的原始 JSON 数据。</param>
public sealed record StreamResultItem(string PayloadType, JsonElement Payload);
