namespace Ability.Hosting;

/// <summary>
/// 宿主传给 Ability 任务进程的调用参数与输入载荷。
/// 业务载荷以原始 JSON 字符串传入，宿主无需依赖具体业务强类型定义。
/// </summary>
/// <param name="LauncherPath">Launcher 可执行文件或程序集路径（如 .exe 或 .dll）。</param>
/// <param name="TaskId">全局唯一的任务标识。</param>
/// <param name="InputTypeName">输入业务模型的类型名称。</param>
/// <param name="InputJson">输入业务模型的 UTF-8 JSON 文本。</param>
/// <param name="OutputTypeName">预期返回的输出类型名（可选，支持业务模型或 DTO）。</param>
/// <param name="Arguments">附加的命令行参数列表（可选）。</param>
/// <param name="EnvironmentVariables">附加注入的环境变量字典（可选）。</param>
/// <param name="WorkingDirectory">子进程工作目录（可选；未指定时默认使用 Launcher 所在目录）。</param>
public sealed record AbilityInvocation(
    string LauncherPath,
    string TaskId,
    string InputTypeName,
    string InputJson,
    string? OutputTypeName = null,
    IReadOnlyList<string>? Arguments = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    string? WorkingDirectory = null);
