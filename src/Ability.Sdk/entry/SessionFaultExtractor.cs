using Ability.Core;

namespace Ability.Sdk;

/// <summary>
/// 负责在任务会话故障时提取安全脱敏后的故障信息，避免内部敏感文件路径或配置外泄。
/// </summary>
internal static class SessionFaultExtractor
{
    /// <summary>
    /// 提取安全脱敏后的故障信息。
    /// 保留请求故障异常的 Kind 与本地调用点 Site，消息仅采用显式审查的安全描述。
    /// </summary>
    /// <param name="exception">捕获的异常对象。</param>
    /// <returns>包含故障类别、脱敏消息与调用点三元组。</returns>
    public static (string Kind, string Message, string Site) Extract(Exception exception)
    {
        if (exception is AbilityRequestFaultException rf)
        {
            var safeDesc = !string.IsNullOrWhiteSpace(rf.SafeDescription)
                ? rf.SafeDescription
                : "Request fault occurred.";
            var site = !string.IsNullOrWhiteSpace(rf.Site) ? rf.Site : "SkillEntry";
            return (rf.Kind, safeDesc, site);
        }

        if (exception is PayloadTooLargeException)
        {
            return ("PayloadTooLarge", "Payload exceeded protocol size limit.", "SkillEntry");
        }

        if (exception is ISafeFaultDescription safe && !string.IsNullOrWhiteSpace(safe.SafeDescription))
        {
            return (exception.GetType().Name, safe.SafeDescription, "SkillEntry");
        }

        if (exception is ProtocolException)
        {
            return (nameof(ProtocolException), "Protocol violation occurred during task execution.", "SkillEntry");
        }

        return (exception.GetType().Name, "Task execution failed.", "SkillEntry");
    }
}
