namespace Ability.Sdk;

/// <summary>
/// 业务流程执行进度描述模型，记录当前阶段、完成量及说明信息。
/// </summary>
public sealed class RunProgress
{
    /// <summary>阶段名称字段。</summary>
    private string _stage = string.Empty;
    /// <summary>已完成工作量字段。</summary>
    private long? _completed;
    /// <summary>总工作量字段。</summary>
    private long? _total;
    /// <summary>计量单位字段。</summary>
    private string _unit = string.Empty;
    /// <summary>说明信息字段。</summary>
    private string? _message;

    /// <summary>
    /// 当前执行阶段名称（必填）。
    /// </summary>
    public required string Stage
    {
        get => _stage;
        init => _stage = value;
    }

    /// <summary>
    /// 当前已完成工作量（为空表示未定量）。
    /// </summary>
    public long? Completed
    {
        get => _completed;
        init => _completed = value;
    }

    /// <summary>
    /// 预估总工作量（为空表示总量未知）。
    /// </summary>
    public long? Total
    {
        get => _total;
        init => _total = value;
    }

    /// <summary>
    /// 工作量计量单位（例如“文件”、“行”、“字节”等）。
    /// </summary>
    public string Unit
    {
        get => _unit;
        init => _unit = value;
    }

    /// <summary>
    /// 附加说明信息。
    /// </summary>
    public string? Message
    {
        get => _message;
        init => _message = value;
    }
}
