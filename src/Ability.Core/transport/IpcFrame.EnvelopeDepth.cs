using System.Text.Json;

namespace Ability.Core;

/// <summary>
/// 提供完整信封 JSON 的协议深度核验。
/// </summary>
public static partial class IpcFrame
{
    /// <summary>
    /// 迭代扫描已编码的完整信封，使用与协议读取器相同的默认最大深度。
    /// </summary>
    /// <param name="envelopeJson">不含长度前缀的完整 UTF-8 JSON 信封。</param>
    /// <exception cref="JsonException">当 JSON 无效或完整信封超过读取端深度上限时抛出。</exception>
    private static void ValidateCompleteEnvelopeDepth(ReadOnlySpan<byte> envelopeJson)
    {
        var reader = new Utf8JsonReader(envelopeJson, new JsonReaderOptions
        {
            MaxDepth = ProtocolJsonContext.Default.Options.MaxDepth
        });

        while (reader.Read())
        {
        }
    }
}
