namespace Ability.Sdk;

/// <summary>
/// 当受管操作单次尝试超过配置的等待期限时抛出的超时异常。
/// 附带操作键与尝试编号，便于调用方与宿主定位超时的具体尝试。
/// </summary>
public sealed class OperationDeadlineExceededException : TimeoutException
{
    /// <summary>
    /// 初始化操作期限超时异常。
    /// </summary>
    /// <param name="operationKey">发生超时的操作键。</param>
    /// <param name="attempt">发生超时的尝试序号。</param>
    public OperationDeadlineExceededException(string operationKey, int attempt)
        : base($"操作 '{operationKey}' 的第 {attempt} 次尝试超过期限。")
    {
        OperationKey = operationKey;
        Attempt = attempt;
    }

    /// <summary>
    /// 操作键标识符。
    /// </summary>
    public string OperationKey { get; }

    /// <summary>
    /// 触发超期的尝试序号。
    /// </summary>
    public int Attempt { get; }
}

/// <summary>
/// 当受管操作单次尝试在触发取消后，未在规定的收尾宽限期内停止执行时抛出的超时异常。
/// 表明底层任务可能处于未响应或挂起状态，SDK 无法保证该次尝试已完全退出。
/// </summary>
public sealed class OperationUnresponsiveException : TimeoutException
{
    /// <summary>
    /// 初始化操作未响应异常。
    /// </summary>
    /// <param name="operationKey">发生未响应的操作键。</param>
    /// <param name="attempt">发生未响应的尝试序号。</param>
    /// <param name="gracePeriod">等待停止的宽限期时限。</param>
    public OperationUnresponsiveException(
        string operationKey,
        int attempt,
        TimeSpan gracePeriod)
        : base($"操作 '{operationKey}' 的第 {attempt} 次尝试在取消后 {gracePeriod} 内未停止。")
    {
        OperationKey = operationKey;
        Attempt = attempt;
        GracePeriod = gracePeriod;
    }

    /// <summary>
    /// 操作键标识符。
    /// </summary>
    public string OperationKey { get; }

    /// <summary>
    /// 未能按时退出的尝试序号。
    /// </summary>
    public int Attempt { get; }

    /// <summary>
    /// 设定的收尾等待宽限时限。
    /// </summary>
    public TimeSpan GracePeriod { get; }
}
