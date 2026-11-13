namespace Ability.Core;

/// <summary>
/// 明确声明可跨进程或向外部导出的安全说明接口。
/// 仅经安全审计、不含敏感数据、路径或原始异常堆栈的说明文本方可实现该接口。
/// </summary>
public interface ISafeFaultDescription
{
    /// <summary>
    /// 获取安全的故障说明文本。
    /// </summary>
    string SafeDescription { get; }
}

/// <summary>
/// 协议级错误异常；表示传输帧或信封损坏、结构不合法或版本不兼容，导致连接不可信。
/// </summary>
public sealed class ProtocolException : Exception, ISafeFaultDescription
{
    /// <summary>
    /// 初始化协议异常实例。
    /// </summary>
    /// <param name="message">错误说明。</param>
    /// <param name="innerException">导致该异常的内部异常。</param>
    public ProtocolException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// 获取可安全导出的协议错误说明。
    /// </summary>
    public string SafeDescription => Message;
}

/// <summary>
/// 本地编码或发送前发现载荷、信封或编码工作空间超限时的异常。
/// 接收端发现对端超限时使用 ProtocolException 表达连接损坏。
/// </summary>
public sealed class PayloadTooLargeException : Exception, ISafeFaultDescription
{
    /// <summary>
    /// 初始化载荷超限异常实例。
    /// </summary>
    /// <param name="message">超限错误说明。</param>
    public PayloadTooLargeException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// 获取可安全导出的超限说明。
    /// </summary>
    public string SafeDescription => Message;
}
