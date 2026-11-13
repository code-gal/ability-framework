using System.Text.Json;

namespace Ability.Model;

/// <summary>
/// 参数补充请求模型，遵循文件切片架构规约使用显式私有字段声明。
/// 包含目标参数键名、面向用户的提示说明以及可选的数据约束（Schema）与前端呈现控件建议（UiDescriptor）。
/// </summary>
public sealed partial class ParamRequestMdl
{
    /// <summary>
    /// 参数键名标识符私有字段。
    /// </summary>
    private string _key = string.Empty;

    /// <summary>
    /// 输入提示信息私有字段。
    /// </summary>
    private string _hint = string.Empty;

    /// <summary>
    /// 数据约束规范私有字段。
    /// </summary>
    private JsonElement? _schema;

    /// <summary>
    /// 前端呈现建议私有字段。
    /// </summary>
    private JsonElement? _uiDescriptor;

    /// <summary>
    /// 参数键名标识符。
    /// </summary>
    public string Key
    {
        get => _key;
        init => _key = value ?? string.Empty;
    }

    /// <summary>
    /// 面向用户或代理的输入提示信息。
    /// </summary>
    public string Hint
    {
        get => _hint;
        init => _hint = value ?? string.Empty;
    }

    /// <summary>
    /// 遵循 JSON Schema Draft 2020-12 的数据约束规范（可选）。
    /// </summary>
    public JsonElement? Schema
    {
        get => _schema;
        init => _schema = value;
    }

    /// <summary>
    /// 纯数据声明的前端输入控件呈现建议（可选，禁止包含任何可执行代码）。
    /// </summary>
    public JsonElement? UiDescriptor
    {
        get => _uiDescriptor;
        init => _uiDescriptor = value;
    }
}
