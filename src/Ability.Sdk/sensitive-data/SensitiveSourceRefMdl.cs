namespace Ability.Model;

/// <summary>
/// 敏感数据源稳定引用模型，遵循文件切片架构规约使用显式私有字段声明。
/// 包含目标数据源资源的 URI 标识以及部署注入的 Bootstrap 密钥引用标识。
/// </summary>
public sealed partial class SensitiveSourceRefMdl
{
    /// <summary>
    /// 数据源资源的稳定 URI 标识私有字段。
    /// </summary>
    private string _sourceUri = string.Empty;

    /// <summary>
    /// 部署注入的 Bootstrap 密钥引用标识私有字段。
    /// </summary>
    private string _bootstrapKeyReference = string.Empty;

    /// <summary>
    /// 数据源资源的稳定 URI 标识（当前实现要求为合法的 file URI）。
    /// </summary>
    public string SourceUri
    {
        get => _sourceUri;
        init => _sourceUri = value ?? string.Empty;
    }

    /// <summary>
    /// 部署注入的 Bootstrap 密钥引用标识（如环境变量名或外部密钥库标识）。
    /// </summary>
    public string BootstrapKeyReference
    {
        get => _bootstrapKeyReference;
        init => _bootstrapKeyReference = value ?? string.Empty;
    }
}
