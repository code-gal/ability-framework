namespace Ability.Sdk;

/// <summary>
/// 受管操作单次尝试达到期限后的到期动作。
/// </summary>
public enum DeadlineAction
{
    /// <summary>
    /// 提醒并通告期限已到，操作可继续执行，由调用方或处理流程决定如何收尾。
    /// </summary>
    Remind,

    /// <summary>
    /// 请求停止操作并将本次尝试判定为超时，视为硬边界。
    /// </summary>
    Cancel
}
