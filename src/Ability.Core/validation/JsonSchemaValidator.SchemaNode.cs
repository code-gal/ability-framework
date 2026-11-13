using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ability.Core;

/// <summary>
/// Schema 语法树的解析分部：按关键字分派读取规则、构建不可变节点，并负责解析期的数值范围检查。
/// 实例校验见 JsonSchemaValidator.SchemaNode.Validate.cs。
/// </summary>
public sealed partial class JsonSchemaValidator
{
    /// <summary>
    /// 解析期规则容器：仅在一次 <see cref="SchemaNode.Parse"/> 调用栈内使用，
    /// 关键字读取步骤写入本容器，全部关键字读取完成后一次性构建不可变的 <see cref="SchemaNode"/>。
    /// </summary>
    private sealed class SchemaRules
    {
        /// <summary>允许的 JSON 类型名称集合。</summary>
        internal List<string> Types { get; } = [];

        /// <summary>允许的枚举值集合。</summary>
        internal List<JsonElement>? Enum { get; set; }

        /// <summary>期望匹配的常量值。</summary>
        internal JsonElement? Const { get; set; }

        /// <summary>对象的属性 Schema 字典。</summary>
        internal Dictionary<string, SchemaNode>? Properties { get; set; }

        /// <summary>对象必需包含的属性名称集合。</summary>
        internal HashSet<string>? Required { get; set; }

        /// <summary>是否允许附加属性。</summary>
        internal bool? AdditionalPropertiesAllowed { get; set; }

        /// <summary>附加属性的校验 Schema 节点。</summary>
        internal SchemaNode? AdditionalPropertiesSchema { get; set; }

        /// <summary>数组元素的校验 Schema 节点。</summary>
        internal SchemaNode? Items { get; set; }

        /// <summary>数组最小元素数。</summary>
        internal int? MinItems { get; set; }

        /// <summary>数组最大元素数。</summary>
        internal int? MaxItems { get; set; }

        /// <summary>字符串最小 Unicode 标量长度。</summary>
        internal int? MinLength { get; set; }

        /// <summary>字符串最大 Unicode 标量长度。</summary>
        internal int? MaxLength { get; set; }

        /// <summary>字符串正则匹配模式。</summary>
        internal Regex? Pattern { get; set; }

        /// <summary>数值最小值（闭区间）。</summary>
        internal ExactNumber? Minimum { get; set; }

        /// <summary>数值最大值（闭区间）。</summary>
        internal ExactNumber? Maximum { get; set; }

        /// <summary>数值下限（开区间）。</summary>
        internal ExactNumber? ExclusiveMinimum { get; set; }

        /// <summary>数值上限（开区间）。</summary>
        internal ExactNumber? ExclusiveMaximum { get; set; }

        /// <summary>数值倍数。</summary>
        internal ExactNumber? MultipleOf { get; set; }
    }

    /// <summary>
    /// Schema 内部语法树节点，保存已解析并规范化的校验约束。
    /// </summary>
    private sealed partial class SchemaNode
    {
        /// <summary>
        /// 允许的 JSON 类型名称集合。
        /// </summary>
        private readonly List<string> _types = [];

        /// <summary>
        /// 允许的枚举值集合。
        /// </summary>
        private readonly List<JsonElement>? _enum;

        /// <summary>
        /// 期望匹配的常量值。
        /// </summary>
        private readonly JsonElement? _const;

        /// <summary>
        /// 对象的属性 Schema 字典。
        /// </summary>
        private readonly Dictionary<string, SchemaNode>? _properties;

        /// <summary>
        /// 对象必需包含的属性名称集合。
        /// </summary>
        private readonly HashSet<string>? _required;

        /// <summary>
        /// 是否允许附加属性。
        /// </summary>
        private readonly bool? _additionalPropertiesAllowed;

        /// <summary>
        /// 附加属性的校验 Schema 节点。
        /// </summary>
        private readonly SchemaNode? _additionalPropertiesSchema;

        /// <summary>
        /// 数组元素的校验 Schema 节点。
        /// </summary>
        private readonly SchemaNode? _items;

        /// <summary>
        /// 数组最小元素数。
        /// </summary>
        private readonly int? _minItems;

        /// <summary>
        /// 数组最大元素数。
        /// </summary>
        private readonly int? _maxItems;

        /// <summary>
        /// 字符串最小 Unicode 标量长度。
        /// </summary>
        private readonly int? _minLength;

        /// <summary>
        /// 字符串最大 Unicode 标量长度。
        /// </summary>
        private readonly int? _maxLength;

        /// <summary>
        /// 字符串正则匹配模式。
        /// </summary>
        private readonly Regex? _pattern;

        /// <summary>
        /// 数值最小值（闭区间）。
        /// </summary>
        private readonly ExactNumber? _minimum;

        /// <summary>
        /// 数值最大值（闭区间）。
        /// </summary>
        private readonly ExactNumber? _maximum;

        /// <summary>
        /// 数值下限（开区间）。
        /// </summary>
        private readonly ExactNumber? _exclusiveMinimum;

        /// <summary>
        /// 数值上限（开区间）。
        /// </summary>
        private readonly ExactNumber? _exclusiveMaximum;

        /// <summary>
        /// 数值倍数。
        /// </summary>
        private readonly ExactNumber? _multipleOf;

        /// <summary>
        /// 是否接受任意值（布尔 true 对应的 Schema）。
        /// </summary>
        private readonly bool _allowsAnything;

        /// <summary>
        /// 是否拒绝所有值（布尔 false 对应的 Schema）。
        /// </summary>
        private readonly bool _rejectsEverything;

        /// <summary>
        /// 初始化布尔 Schema 节点。
        /// </summary>
        /// <param name="allowsAnything">是否接受任意值。</param>
        /// <param name="rejectsEverything">是否拒绝所有值。</param>
        private SchemaNode(bool allowsAnything, bool rejectsEverything)
        {
            _allowsAnything = allowsAnything;
            _rejectsEverything = rejectsEverything;
        }

        /// <summary>
        /// 用解析完成的规则容器初始化不可变节点。
        /// </summary>
        /// <param name="rules">已由关键字读取步骤填充的规则容器。</param>
        private SchemaNode(SchemaRules rules)
        {
            _types = rules.Types;
            _enum = rules.Enum;
            _const = rules.Const;
            _properties = rules.Properties;
            _required = rules.Required;
            _additionalPropertiesAllowed = rules.AdditionalPropertiesAllowed;
            _additionalPropertiesSchema = rules.AdditionalPropertiesSchema;
            _items = rules.Items;
            _minItems = rules.MinItems;
            _maxItems = rules.MaxItems;
            _minLength = rules.MinLength;
            _maxLength = rules.MaxLength;
            _pattern = rules.Pattern;
            _minimum = rules.Minimum;
            _maximum = rules.Maximum;
            _exclusiveMinimum = rules.ExclusiveMinimum;
            _exclusiveMaximum = rules.ExclusiveMaximum;
            _multipleOf = rules.MultipleOf;
        }

        /// <summary>
        /// 递归解析 JSON 元素为 SchemaNode 语法树节点：先检查深度与 schema 形态，
        /// 再逐关键字填充规则容器，最后构建不可变节点。
        /// </summary>
        /// <param name="schema">待解析的 JSON Schema 元素。</param>
        /// <param name="path">当前解析路径。</param>
        /// <param name="depth">当前递归深度。</param>
        /// <returns>解析得到的 SchemaNode 实例。</returns>
        internal static SchemaNode Parse(JsonElement schema, string path, int depth)
        {
            if (depth > MaxSchemaDepth)
            {
                throw new SchemaNotSupportedException("depth", path, $"Schema 嵌套深度超过允许上限 {MaxSchemaDepth}。");
            }

            if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return new SchemaNode(schema.ValueKind == JsonValueKind.True, schema.ValueKind == JsonValueKind.False);
            }

            if (schema.ValueKind != JsonValueKind.Object)
            {
                throw new SchemaNotSupportedException("<非对象 schema>", path, "schema 必须是对象或布尔值");
            }

            var rules = new SchemaRules();
            foreach (var keyword in schema.EnumerateObject())
            {
                ParseKeyword(keyword, rules, path, depth);
            }

            return new SchemaNode(rules);
        }

        /// <summary>
        /// 按关键字分派解析：受支持关键字写入规则容器，注解关键字忽略，其余关键字明确报告不受支持。
        /// 本节是支持范围的唯一分派位置，新增关键字只在此登记一次。
        /// </summary>
        /// <param name="keyword">当前关键字属性。</param>
        /// <param name="rules">本次解析的规则容器。</param>
        /// <param name="path">当前解析路径。</param>
        /// <param name="depth">当前递归深度。</param>
        private static void ParseKeyword(JsonProperty keyword, SchemaRules rules, string path, int depth)
        {
            var keywordPath = $"{path}.{keyword.Name}";
            switch (keyword.Name)
            {
                case "type":
                    ReadTypes(keyword.Value, keywordPath, rules);
                    break;
                case "enum":
                    ReadEnum(keyword.Value, keywordPath, depth, rules);
                    break;
                case "const":
                    rules.Const = ReadConst(keyword.Value, keywordPath, depth);
                    break;
                case "properties":
                    ReadProperties(keyword.Value, keywordPath, depth, rules);
                    break;
                case "required":
                    ReadRequired(keyword.Value, keywordPath, rules);
                    break;
                case "additionalProperties":
                    ReadAdditionalProperties(keyword.Value, keywordPath, depth, rules);
                    break;
                case "items":
                    rules.Items = Parse(keyword.Value, keywordPath, depth + 1);
                    break;
                case "minItems":
                    rules.MinItems = ReadCount(keyword.Name, keyword.Value, keywordPath);
                    break;
                case "maxItems":
                    rules.MaxItems = ReadCount(keyword.Name, keyword.Value, keywordPath);
                    break;
                case "minLength":
                    rules.MinLength = ReadCount(keyword.Name, keyword.Value, keywordPath);
                    break;
                case "maxLength":
                    rules.MaxLength = ReadCount(keyword.Name, keyword.Value, keywordPath);
                    break;
                case "pattern":
                    rules.Pattern = ReadPattern(keyword.Value, keywordPath);
                    break;
                case "minimum":
                    rules.Minimum = ReadNumber(keyword.Name, keyword.Value, keywordPath);
                    break;
                case "maximum":
                    rules.Maximum = ReadNumber(keyword.Name, keyword.Value, keywordPath);
                    break;
                case "exclusiveMinimum":
                    rules.ExclusiveMinimum = ReadNumber(keyword.Name, keyword.Value, keywordPath);
                    break;
                case "exclusiveMaximum":
                    rules.ExclusiveMaximum = ReadNumber(keyword.Name, keyword.Value, keywordPath);
                    break;
                case "multipleOf":
                    rules.MultipleOf = ReadMultipleOf(keyword.Value, keywordPath);
                    break;
                case "$schema" or "$id" or "title" or "description" or "default" or "examples" or "format" or "$comment":
                    break;
                default:
                    throw new SchemaNotSupportedException(keyword.Name, path);
            }
        }

        /// <summary>
        /// 读取 type 关键字：单个类型名，或非空、元素唯一的类型名数组。
        /// </summary>
        /// <param name="value">关键字取值。</param>
        /// <param name="path">当前解析路径。</param>
        /// <param name="rules">本次解析的规则容器。</param>
        private static void ReadTypes(JsonElement value, string path, SchemaRules rules)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                rules.Types.Add(ReadTypeName(value.GetString()!, path));
                return;
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                throw new SchemaNotSupportedException("type", path, "必须是字符串或字符串数组");
            }

            if (value.GetArrayLength() == 0)
            {
                throw new SchemaNotSupportedException("type", path, "数组至少要有一个元素");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new SchemaNotSupportedException("type", path, "数组元素必须是字符串");
                }

                var typeName = ReadTypeName(item.GetString()!, path);
                if (!seen.Add(typeName))
                {
                    throw new SchemaNotSupportedException("type", path, "数组元素不能重复");
                }

                rules.Types.Add(typeName);
            }
        }

        /// <summary>
        /// 读取 enum 关键字：数组，元素克隆保存并逐个检查数值范围。
        /// </summary>
        /// <param name="value">关键字取值。</param>
        /// <param name="path">当前解析路径。</param>
        /// <param name="depth">当前递归深度。</param>
        /// <param name="rules">本次解析的规则容器。</param>
        private static void ReadEnum(JsonElement value, string path, int depth, SchemaRules rules)
        {
            if (value.ValueKind != JsonValueKind.Array)
            {
                throw new SchemaNotSupportedException("enum", path, "必须是数组");
            }

            var values = new List<JsonElement>();
            foreach (var item in value.EnumerateArray())
            {
                ValidateJsonNumberRange(item, $"{path}[{values.Count}]", "enum", depth + 1);
                values.Add(item.Clone());
            }

            rules.Enum = values;
        }

        /// <summary>
        /// 读取 const 关键字：任意 JSON 值克隆保存，数值须在精确求值范围内。
        /// </summary>
        /// <param name="value">关键字取值。</param>
        /// <param name="path">当前解析路径。</param>
        /// <param name="depth">当前递归深度。</param>
        /// <returns>克隆后的常量值。</returns>
        private static JsonElement ReadConst(JsonElement value, string path, int depth)
        {
            ValidateJsonNumberRange(value, path, "const", depth + 1);
            return value.Clone();
        }

        /// <summary>
        /// 读取 properties 关键字：对象，每个属性值递归解析为子节点。
        /// </summary>
        /// <param name="value">关键字取值。</param>
        /// <param name="path">当前解析路径。</param>
        /// <param name="depth">当前递归深度。</param>
        /// <param name="rules">本次解析的规则容器。</param>
        private static void ReadProperties(JsonElement value, string path, int depth, SchemaRules rules)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new SchemaNotSupportedException("properties", path, "必须是对象");
            }

            var properties = new Dictionary<string, SchemaNode>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                properties[property.Name] = Parse(property.Value, $"{path}.{property.Name}", depth + 1);
            }

            rules.Properties = properties;
        }

        /// <summary>
        /// 读取 required 关键字：字符串数组，元素不能重复。
        /// </summary>
        /// <param name="value">关键字取值。</param>
        /// <param name="path">当前解析路径。</param>
        /// <param name="rules">本次解析的规则容器。</param>
        private static void ReadRequired(JsonElement value, string path, SchemaRules rules)
        {
            if (value.ValueKind != JsonValueKind.Array)
            {
                throw new SchemaNotSupportedException("required", path, "必须是数组");
            }

            var required = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new SchemaNotSupportedException("required", path, "元素必须是字符串");
                }

                if (!required.Add(item.GetString()!))
                {
                    throw new SchemaNotSupportedException("required", path, "元素不能重复");
                }
            }

            rules.Required = required;
        }

        /// <summary>
        /// 读取 additionalProperties 关键字：布尔值表示是否允许附加属性，对象则递归解析为附加属性规则。
        /// </summary>
        /// <param name="value">关键字取值。</param>
        /// <param name="path">当前解析路径。</param>
        /// <param name="depth">当前递归深度。</param>
        /// <param name="rules">本次解析的规则容器。</param>
        private static void ReadAdditionalProperties(JsonElement value, string path, int depth, SchemaRules rules)
        {
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                rules.AdditionalPropertiesAllowed = value.ValueKind == JsonValueKind.True;
                return;
            }

            rules.AdditionalPropertiesSchema = Parse(value, path, depth + 1);
        }

        /// <summary>
        /// 读取 pattern 关键字：字符串且必须是合法 .NET 正则，编译时设置匹配期限。
        /// </summary>
        /// <param name="value">关键字取值。</param>
        /// <param name="path">当前解析路径。</param>
        /// <returns>已编译并带匹配期限的正则实例。</returns>
        private static Regex ReadPattern(JsonElement value, string path)
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                throw new SchemaNotSupportedException("pattern", path, "必须是字符串");
            }

            try
            {
                return new Regex(value.GetString()!, RegexOptions.None, PatternTimeout);
            }
            catch (ArgumentException)
            {
                throw new SchemaNotSupportedException("pattern", path, "正则语法无效");
            }
        }

        /// <summary>
        /// 读取并校验 JSON 类型名称是否合法。
        /// </summary>
        /// <param name="name">类型名称字符串。</param>
        /// <param name="path">当前解析路径。</param>
        /// <returns>合法的类型名称。</returns>
        private static string ReadTypeName(string name, string path)
            => name is "object" or "array" or "string" or "number" or "integer" or "boolean" or "null"
                ? name
                : throw new SchemaNotSupportedException("type", path, "不是 JSON 类型名");

        /// <summary>
        /// 读取非负整数类型的计数值关键字。
        /// </summary>
        /// <param name="keyword">关键字名称。</param>
        /// <param name="value">包含数值的 JSON 元素。</param>
        /// <param name="path">当前解析路径。</param>
        /// <returns>解析后的整数值。</returns>
        private static int ReadCount(string keyword, JsonElement value, string path)
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count) || count < 0)
            {
                throw new SchemaNotSupportedException(keyword, path, "必须是非负整数");
            }

            return count;
        }

        /// <summary>
        /// 读取 multipleOf 关键字：先按精确数值解析，再要求大于零。
        /// </summary>
        /// <param name="value">关键字取值。</param>
        /// <param name="path">当前解析路径。</param>
        /// <returns>解析后的精确倍数。</returns>
        private static ExactNumber ReadMultipleOf(JsonElement value, string path)
        {
            var multipleOf = ReadNumber("multipleOf", value, path);
            if (multipleOf.IsZero || multipleOf.Significand.Sign < 0)
            {
                throw new SchemaNotSupportedException("multipleOf", path, "必须大于 0");
            }

            return multipleOf;
        }

        /// <summary>
        /// 从 JSON 元素中读取精确十进制数值。
        /// </summary>
        /// <param name="keyword">关键字名称。</param>
        /// <param name="value">包含数值的 JSON 元素。</param>
        /// <param name="path">当前解析路径。</param>
        /// <returns>解析后的精确数值。</returns>
        private static ExactNumber ReadNumber(string keyword, JsonElement value, string path)
        {
            if (value.ValueKind != JsonValueKind.Number)
            {
                throw new SchemaNotSupportedException(keyword, path, "必须是数值");
            }

            if (!TryParseNumber(value.GetRawText(), out var number))
            {
                throw new SchemaNotSupportedException(
                    keyword,
                    path,
                    $"数值超出可精确判定范围（十进制指数绝对值不超过 {ExactNumber.MaxExponentMagnitude}）");
            }

            return number;
        }

        /// <summary>
        /// 校验给定 JSON 元素内所有数值是否均在精确十进制表示范围内。
        /// </summary>
        /// <param name="value">待检查的 JSON 元素。</param>
        /// <param name="path">当前解析路径。</param>
        /// <param name="keyword">关键字名称。</param>
        /// <param name="depth">当前递归深度。</param>
        private static void ValidateJsonNumberRange(JsonElement value, string path, string keyword, int depth)
        {
            if (depth > MaxSchemaDepth)
            {
                throw new SchemaNotSupportedException("depth", path, $"Schema 嵌套深度超过允许上限 {MaxSchemaDepth}。");
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.Number when !TryParseNumber(value.GetRawText(), out _):
                    throw new SchemaNotSupportedException(keyword, path, "数值超出精确比较范围");
                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        ValidateJsonNumberRange(item, $"{path}[{index++}]", keyword, depth + 1);
                    }

                    break;
                case JsonValueKind.Object:
                    foreach (var property in value.EnumerateObject())
                    {
                        ValidateJsonNumberRange(property.Value, $"{path}.{property.Name}", keyword, depth + 1);
                    }

                    break;
            }
        }
    }
}
