namespace Ability.Sdk;

/// <summary>
/// 租约模式，表达访问受管资源时的操作意图。
/// </summary>
public enum LeaseMode
{
    /// <summary>
    /// 只读访问意图。
    /// </summary>
    Read,

    /// <summary>
    /// 独占或排他写入意图。
    /// </summary>
    Write,

    /// <summary>
    /// 调用或执行外部受管能力意图。
    /// </summary>
    Invoke
}
