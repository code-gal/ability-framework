namespace Ability.Core;

/// <summary>
/// JSON Schema 关键字不受支持或定义写法无效异常。
/// 解析期发现不支持的关键字或非法定义时立即失败，避免静默忽略导致把未校验的数据报为通过。
/// </summary>
public sealed class SchemaNotSupportedException : Exception, ISafeFaultDescription
{
    /// <summary>
    /// 初始化不受支持关键字异常实例。
    /// </summary>
    /// <param name="keyword">关键字名称。</param>
    /// <param name="path">关键字在 Schema 中的路径。</param>
    /// <param name="detail">可选的详细说明。</param>
    public SchemaNotSupportedException(string keyword, string path, string? detail = null)
        : base(detail is null
            ? $"JSON Schema 关键字 {keyword} 在 {path} 处不受支持。"
            : $"JSON Schema 关键字 {keyword} 在 {path} 处无效：{detail}")
    {
        Keyword = keyword;
        Path = path;
    }

    /// <summary>
    /// 获取引发异常的 Schema 关键字名称。
    /// </summary>
    public string Keyword { get; }

    /// <summary>
    /// 获取关键字在 Schema 中的位置路径（如 "$.properties.count.minimum"）。
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// 获取可安全导出的故障说明；仅包含关键字、路径与固定原因，不包含数据值。
    /// </summary>
    public string SafeDescription => Message;
}

/// <summary>
/// JSON Schema 校验过程因超时或超出求值资源限制而未完成异常。
/// 表示校验由于正则匹配超时或数值超出求值上限未能得出确定结论，调用方不能将其视为校验通过或单纯的数据违规。
/// </summary>
public sealed class SchemaValidationIncompleteException : Exception, ISafeFaultDescription
{
    /// <summary>
    /// 初始化校验未完成异常实例。
    /// </summary>
    /// <param name="keyword">涉及的关键字名称。</param>
    /// <param name="path">涉及的实例数据路径。</param>
    /// <param name="detail">具体的未完成原因说明；为空时默认为超时。</param>
    public SchemaValidationIncompleteException(string keyword, string path, string? detail = null)
        : base(detail is null
            ? $"JSON Schema 校验未完成：{path} 的 {keyword} 判断超时。"
            : $"JSON Schema 校验未完成：{path} 的 {keyword} {detail}。")
    {
        Keyword = keyword;
        Path = path;
    }

    /// <summary>
    /// 获取涉及的 Schema 关键字名称。
    /// </summary>
    public string Keyword { get; }

    /// <summary>
    /// 获取发生超限或超时的实例数据路径。
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// 获取可安全导出的未完成说明。
    /// </summary>
    public string SafeDescription => Message;
}
