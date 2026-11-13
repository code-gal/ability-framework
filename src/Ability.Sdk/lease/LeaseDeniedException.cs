using Ability.Core;

namespace Ability.Sdk;

/// <summary>
/// 宿主拒绝租约申请时抛出的可识别异常。
/// 实现 <see cref="ISafeFaultDescription"/> 接口，仅导出脱敏后的资源引用，来自上游的拒绝细节留给本地诊断。
/// </summary>
public sealed class LeaseDeniedException : Exception, ISafeFaultDescription
{
    /// <summary>
    /// 初始化租约被拒绝异常。
    /// </summary>
    /// <param name="resource">申请的资源标识 URI 或路径。</param>
    /// <param name="reason">宿主返回的拒绝原因说明。</param>
    public LeaseDeniedException(string resource, string reason)
        : base($"租约被拒绝：{resource}；{reason}")
    {
        Resource = resource;
        Reason = reason;
    }

    /// <summary>
    /// 申请的受管资源标识。
    /// </summary>
    public string Resource { get; }

    /// <summary>
    /// 宿主仲裁返回的拒绝详细原因。
    /// </summary>
    public string Reason { get; }

    /// <summary>
    /// 安全导出的错误描述：仅包含脱敏后的资源标识，不泄露上游内部拒绝细节与认证信息。
    /// </summary>
    public string SafeDescription => $"租约被拒绝：{SanitizeReference(Resource)}";

    /// <summary>
    /// 对资源引用执行脱敏处理：剥离查询参数（?）、URL 片段（#）以及 authority 中的用户认证信息（user:pass@）。
    /// </summary>
    /// <param name="reference">原始资源引用字符串。</param>
    /// <returns>脱敏后的安全引用路径。</returns>
    internal static string SanitizeReference(string reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return string.Empty;
        }

        var trimmed = reference;
        var separator = trimmed.IndexOfAny(['?', '#']);
        if (separator >= 0)
        {
            trimmed = trimmed[..separator];
        }

        int authorityStart;
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            authorityStart = 2;
        }
        else
        {
            var schemeIndex = trimmed.IndexOf("://", StringComparison.Ordinal);
            authorityStart = schemeIndex < 0 ? 0 : schemeIndex + 3;
        }

        return StripUserInfo(trimmed, authorityStart);
    }

    /// <summary>
    /// 从权限段中剥除用户口令（user:pass@）部分。
    /// </summary>
    private static string StripUserInfo(string reference, int authorityStart)
    {
        var authorityEnd = reference.IndexOf('/', authorityStart);
        var authority = authorityEnd < 0 ? reference[authorityStart..] : reference[authorityStart..authorityEnd];
        var at = authority.LastIndexOf('@');
        if (at < 0)
        {
            return reference;
        }

        var host = authority[(at + 1)..];
        var suffix = authorityEnd < 0 ? string.Empty : reference[authorityEnd..];
        return reference[..authorityStart] + host + suffix;
    }
}
