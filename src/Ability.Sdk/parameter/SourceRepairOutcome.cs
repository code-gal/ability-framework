namespace Ability.Sdk;

/// <summary>
/// 数据源恢复请求仲裁结果枚举。
/// 表示宿主针对失效数据源做出的后续动作决断。
/// </summary>
public enum SourceRepairOutcome
{
    /// <summary>
    /// 数据源已就地热更新恢复（如 Token 已刷新或本地配置文件已重载），技能可在当前进程内重读数据后继续尝试。
    /// </summary>
    Reread,

    /// <summary>
    /// 外部底层来源需要进程重启方可生效（如环境变量或系统配置），需要由宿主进程护栏重启技能进程。
    /// </summary>
    RestartRequired,

    /// <summary>
    /// 宿主拒绝恢复请求，或数据源无法修复。
    /// </summary>
    Denied,

    /// <summary>
    /// 恢复流程已被取消。
    /// </summary>
    Cancelled
}
