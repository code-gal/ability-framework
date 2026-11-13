namespace Ability.Hosting;

/// <summary>
/// 任务专属临时目录进程归属登记失败异常。
/// 当技能进程未能成功持久化到目录的归属标记文件时抛出，指示目录归属无法确认并触发该进程的安全回收。
/// </summary>
public sealed class TaskTempOwnershipException : InvalidOperationException
{
    /// <summary>
    /// 初始化 <see cref="TaskTempOwnershipException"/> 类的新实例。
    /// </summary>
    public TaskTempOwnershipException()
    {
    }

    /// <summary>
    /// 使用指定的错误消息初始化 <see cref="TaskTempOwnershipException"/> 类的新实例。
    /// </summary>
    /// <param name="message">解释异常原因的错误消息。</param>
    public TaskTempOwnershipException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// 使用指定的错误消息和导致此异常的内部异常初始化 <see cref="TaskTempOwnershipException"/> 类的新实例。
    /// </summary>
    /// <param name="message">解释异常原因的错误消息。</param>
    /// <param name="innerException">导致当前异常的内部异常。</param>
    public TaskTempOwnershipException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
