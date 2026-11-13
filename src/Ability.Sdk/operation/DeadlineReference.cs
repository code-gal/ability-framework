namespace Ability.Sdk;

/// <summary>
/// 期限计时的锚点起点参考。
/// 决定在动态修改运行策略时，剩余等待期限是保留本次尝试的原始起点，还是自本次更新生效起重新计算。
/// </summary>
public enum DeadlineReference
{
    /// <summary>
    /// 以本次尝试的开始时刻为锚点，运行期间更新策略不重置已消耗的时间。
    /// </summary>
    FromStart,

    /// <summary>
    /// 以本次策略生效时刻为锚点，更新生效即重新起算等待期限。
    /// </summary>
    FromUpdate
}
