using Ability.Core;

namespace Ability.Sdk;

/// <summary>
/// 当通信对端以 RequestFault 答复某项请求时抛出的 I/O 异常。
/// 保留错误种类（<see cref="Kind"/>）、调用位置（<see cref="Site"/>）与原始错误消息。
/// 实现 <see cref="ISafeFaultDescription"/> 接口，只有 SDK 明确审查过的安全信息才允许跨进程导出。
/// </summary>
public class AbilityRequestFaultException : IOException, ISafeFaultDescription
{
    /// <summary>
    /// 初始化请求故障异常。
    /// </summary>
    /// <param name="kind">错误分类标识。</param>
    /// <param name="message">错误详情说明。</param>
    /// <param name="site">发生故障的调用位置标识（可选）。</param>
    /// <param name="safeDescription">可导出的安全描述文本（可选）。</param>
    public AbilityRequestFaultException(
        string kind,
        string message,
        string? site = null,
        string? safeDescription = null)
        : base($"宿主请求故障 {kind}：{message}")
    {
        Kind = kind;
        Site = site;
        SafeDescription = safeDescription;
    }

    /// <summary>
    /// 错误分类标识（如 "UnsupportedRequest"、"DuplicateOperation"）。
    /// </summary>
    public string Kind { get; }

    /// <summary>
    /// 发生故障的调用位置标识。
    /// </summary>
    public string? Site { get; }

    /// <summary>
    /// 可跨进程导出的安全描述文本；未显式给出时为 null。
    /// </summary>
    public string? SafeDescription { get; }

    /// <summary>
    /// 契约接口实现：未声明安全描述时返回空字符串，杜绝未经审查的外部错误内容泄露。
    /// </summary>
    string ISafeFaultDescription.SafeDescription => SafeDescription ?? string.Empty;

    /// <summary>
    /// 构造已由 SDK 自身审查过安全性的请求故障异常。
    /// 保证传入的说明文本仅含受控的结构化信息，可作为安全描述导出。
    /// </summary>
    /// <param name="kind">错误分类标识。</param>
    /// <param name="message">已由内部审查的安全说明。</param>
    /// <param name="site">调用位置标识（可选）。</param>
    /// <returns>构建的异常实例。</returns>
    internal static AbilityRequestFaultException Reviewed(string kind, string message, string? site = null)
        => new(kind, message, site, safeDescription: message);
}

/// <summary>
/// 当上游交付的补参数据不满足请求声明的 JSON Schema 时抛出的模式违规异常。
/// 校验摘要由本进程内的 Schema 校验器生成，仅描述字段结构违规，可安全导出。
/// </summary>
public sealed class AbilitySchemaViolationException : AbilityRequestFaultException
{
    /// <summary>
    /// 初始化 Schema 违规异常。
    /// </summary>
    /// <param name="message">Schema 校验失败原因摘要。</param>
    public AbilitySchemaViolationException(string message)
        : base("SchemaViolation", message, site: null, safeDescription: message)
    {
    }
}

/// <summary>
/// 当等待原语操作期间底层通信连接断开时抛出的异常。
/// 继承 <see cref="IOException"/> 以明确区分网络传输故障与业务逻辑失败。
/// </summary>
public sealed class AbilityDisconnectedException : IOException, ISafeFaultDescription
{
    /// <summary>
    /// 初始化断连异常。
    /// </summary>
    /// <param name="message">断开原因说明。</param>
    public AbilityDisconnectedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// 本地 SDK 描述的断连原因可作为安全说明导出。
    /// </summary>
    public string SafeDescription => Message;
}
