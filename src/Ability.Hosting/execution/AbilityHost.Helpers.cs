using System.Text;
using System.Text.Json;
using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// Ability 宿主公开入口的辅助分部：接入前的输入预检与遗留目录清理入口。
/// </summary>
public static partial class AbilityHost
{
    /// <summary>
    /// 预检并解析输入的 JSON 载荷，核验大小上限与语法合法性。
    /// </summary>
    /// <param name="inputJson">输入的 JSON 文本。</param>
    /// <param name="paramName">参数名称。</param>
    /// <returns>解析出的根 JsonElement 副本。</returns>
    private static JsonElement ValidateAndParseInputPayload(string inputJson, string paramName)
    {
        var inputByteCount = Encoding.UTF8.GetByteCount(inputJson);
        if (inputByteCount > ProtocolConstants.MaxPayloadBytes)
        {
            throw new ArgumentException(
                $"输入载荷字节数 ({inputByteCount}) 超出协议单载荷上限 ({ProtocolConstants.MaxPayloadBytes} 字节)。",
                paramName);
        }

        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"输入载荷不是合法的 JSON: {ex.Message}", paramName, ex);
        }
    }

    /// <summary>
    /// 获取对外部观察安全的异常简要描述。
    /// </summary>
    /// <param name="ex">异常对象。</param>
    /// <returns>安全的异常描述文本或异常类型名。</returns>
    private static string DescribeSafeError(Exception ex) =>
        ex is ISafeFaultDescription safe && !string.IsNullOrWhiteSpace(safe.SafeDescription)
            ? safe.SafeDescription
            : ex.GetType().Name;

    /// <summary>
    /// 清理满足安全回收条件的遗留任务临时目录。
    /// 仅清理标记处于 releasing 状态且关联进程均已退出的目录，未确认退出或被异常中断的目录予以保留并输出诊断。
    /// </summary>
    /// <param name="options">可选的宿主运行选项。</param>
    /// <returns>成功清理的遗留目录数量。</returns>
    public static int CleanupTaskTempLeftovers(HostRuntimeOptions? options = null)
    {
        options ??= new HostRuntimeOptions();
        return TaskTempDirectory.CleanupLeftovers(options);
    }
}
