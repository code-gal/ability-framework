namespace Ability.Sdk;

/// <summary>
/// 负责从技能进程启动参数中解析 IPC 管道与运行时配置的命令行解析器。
/// </summary>
internal static class SessionCommandLine
{
    /// <summary>
    /// 从启动命令行参数中解析 --ipc 命名管道名称。
    /// 严格核验恰好包含一个 --ipc 选项且参数值非空白，杜绝歧义或重复配置。
    /// </summary>
    /// <param name="args">命令行参数数组。</param>
    /// <param name="pipeName">输出管道名称。</param>
    /// <param name="error">若解析失败输出错误提示。</param>
    /// <returns>解析成功返回 true；否则返回 false。</returns>
    public static bool TryGetPipeName(string[] args, out string pipeName, out string? error)
    {
        pipeName = string.Empty;
        error = null;

        var ipcCount = 0;
        var valueIndex = -1;

        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--ipc", StringComparison.OrdinalIgnoreCase))
            {
                ipcCount++;
                valueIndex = i + 1;
            }
        }

        if (ipcCount == 0)
        {
            error = "启动参数中缺少必需的 --ipc <pipeName> 选项。";
            return false;
        }

        if (ipcCount > 1)
        {
            error = "启动参数中包含多个重复的 --ipc 选项。";
            return false;
        }

        if (valueIndex >= args.Length || string.IsNullOrWhiteSpace(args[valueIndex]))
        {
            error = "--ipc 选项缺少参数值。";
            return false;
        }

        pipeName = args[valueIndex];
        return true;
    }
}
