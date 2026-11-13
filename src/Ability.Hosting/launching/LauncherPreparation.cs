namespace Ability.Hosting;

/// <summary>
/// 已就绪的 Launcher 启动定义，包含可执行文件名、前缀参数与工作目录。
/// </summary>
/// <param name="FileName">要启动的实际文件名（如 "dotnet" 或 native exe 路径）。</param>
/// <param name="PrefixArguments">排在命令行最前面的固定参数（例如托管 dll 路径）。</param>
/// <param name="WorkingDirectory">子进程的工作目录。</param>
public sealed record PreparedLauncher(
    string FileName,
    IReadOnlyList<string> PrefixArguments,
    string WorkingDirectory);

/// <summary>
/// Launcher 启动准备工具：分析启动路径，标准化可执行文件与托管 dll 调用方式。
/// </summary>
public static class LauncherPreparation
{
    /// <summary>
    /// 为给定的 Launcher 路径准备启动定义。
    /// </summary>
    /// <param name="launcherPath">Launcher 可执行文件或程序集路径。</param>
    /// <param name="workingDirectoryOverride">可选的工作目录覆盖。</param>
    /// <returns>标准化后的启动定义。</returns>
    /// <exception cref="FileNotFoundException">当文件不存在时抛出。</exception>
    public static PreparedLauncher Prepare(string launcherPath, string? workingDirectoryOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherPath);
        var fullPath = Path.GetFullPath(launcherPath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"未找到指定的 Launcher 文件：“{fullPath}”。", fullPath);
        }

        var workDir = !string.IsNullOrWhiteSpace(workingDirectoryOverride)
            ? Path.GetFullPath(workingDirectoryOverride)
            : Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();

        if (string.Equals(Path.GetExtension(fullPath), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            return new PreparedLauncher("dotnet", [fullPath], workDir);
        }

        return new PreparedLauncher(fullPath, [], workDir);
    }
}
