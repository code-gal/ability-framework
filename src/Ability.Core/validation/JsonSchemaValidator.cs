using System.Collections.Concurrent;
using System.Text.Json;

namespace Ability.Core;

/// <summary>
/// 宿主与技能共用的 JSON Schema（Draft 2020-12）解析与验证器。
/// 在各进程内独立复用解析结果，提供严格确定的校验语义与有界资源保障。
/// Schema 语法树的解析与实例校验分别置于 JsonSchemaValidator.SchemaNode.cs 与
/// JsonSchemaValidator.SchemaNode.Validate.cs，本文件保留对外入口、缓存与值等价比较。
/// </summary>
public sealed partial class JsonSchemaValidator
{
    /// <summary>
    /// Schema 缓存容量上限。
    /// </summary>
    private const int MaxCachedSchemas = 64;

    /// <summary>
    /// Schema 及实例解析的最大允许嵌套深度。
    /// </summary>
    private const int MaxSchemaDepth = 64;

    /// <summary>
    /// 已解析验证器的文本哈希缓存字典。
    /// </summary>
    private static readonly ConcurrentDictionary<string, JsonSchemaValidator> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// 缓存并发插入与清理互斥锁。
    /// </summary>
    private static readonly object CacheGate = new();

    /// <summary>
    /// 正则表达式单次匹配超时时间。
    /// </summary>
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 根 Schema 语法树节点。
    /// </summary>
    private readonly SchemaNode _root;

    /// <summary>
    /// 初始化验证器实例。
    /// </summary>
    /// <param name="root">根 Schema 语法树节点。</param>
    private JsonSchemaValidator(SchemaNode root) => _root = root;

    /// <summary>
    /// 根据原始 Schema JSON 文本复用已解析的验证器实例。
    /// 缓存有界（最多保留 64 个），超限时自动清空。
    /// </summary>
    /// <param name="schema">Schema JSON 元素。</param>
    /// <returns>已解析的验证器实例。</returns>
    public static JsonSchemaValidator For(JsonElement schema)
    {
        var text = schema.GetRawText();
        if (Cache.TryGetValue(text, out var cached))
        {
            return cached;
        }

        lock (CacheGate)
        {
            if (Cache.TryGetValue(text, out cached))
            {
                return cached;
            }

            var validator = Parse(schema);
            if (Cache.Count >= MaxCachedSchemas)
            {
                Cache.Clear();
            }

            Cache[text] = validator;
            return validator;
        }
    }

    /// <summary>
    /// 解析一份 JSON Schema。
    /// 遇到不支持的关键字或非法定义时抛出 <see cref="SchemaNotSupportedException"/>。
    /// </summary>
    /// <param name="schema">Schema JSON 元素。</param>
    /// <returns>已解析的验证器实例。</returns>
    public static JsonSchemaValidator Parse(JsonElement schema)
        => new(SchemaNode.Parse(schema, "$", 0));

    /// <summary>
    /// 校验给定的 JSON 实例数据。
    /// </summary>
    /// <param name="instance">待校验的数据实例。</param>
    /// <returns>校验结果，包含是否合法及全部违规项列表。</returns>
    /// <exception cref="SchemaValidationIncompleteException">当正则匹配超时或数值超出求值范围导致校验无法完成时抛出。</exception>
    public SchemaValidationResult Validate(JsonElement instance)
    {
        var violations = new List<SchemaViolation>();
        _root.Validate(instance, "$", violations, 0);
        return new SchemaValidationResult(violations.Count == 0, violations);
    }

    /// <summary>
    /// 尝试将原始文本解析为精确十进制数值。
    /// </summary>
    /// <param name="text">待解析的数值文本。</param>
    /// <param name="number">解析输出的 ExactNumber 结构。</param>
    /// <returns>解析成功返回 true；超出范围或格式错误返回 false。</returns>
    private static bool TryParseNumber(string text, out ExactNumber number)
        => ExactNumber.TryParse(text, out number);

    /// <summary>
    /// 深度比较两个 JSON 元素的值等价性（支持对象、数组、字符串、数值的精确比较）。
    /// </summary>
    /// <param name="left">左侧元素。</param>
    /// <param name="right">右侧元素。</param>
    /// <param name="path">当前 JSON 路径。</param>
    /// <param name="depth">当前递归深度。</param>
    /// <returns>值相等返回 true；否则返回 false。</returns>
    private static bool ValuesEqual(JsonElement left, JsonElement right, string path, int depth)
    {
        if (depth > MaxSchemaDepth)
        {
            throw new SchemaValidationIncompleteException("depth", path, $"实例嵌套深度超过允许上限 {MaxSchemaDepth}。");
        }

        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Number:
                if (!TryParseNumber(left.GetRawText(), out var leftNumber)
                    || !TryParseNumber(right.GetRawText(), out var rightNumber))
                {
                    throw new SchemaValidationIncompleteException(
                        "const/enum",
                        path,
                        $"数值超出可求值范围（十进制指数绝对值不超过 {ExactNumber.MaxExponentMagnitude}）。");
                }

                return ExactNumber.Compare(leftNumber, rightNumber) == 0;

            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);

            case JsonValueKind.Array:
            {
                if (left.GetArrayLength() != right.GetArrayLength())
                {
                    return false;
                }

                var index = 0;
                var leftItems = left.EnumerateArray().GetEnumerator();
                var rightItems = right.EnumerateArray().GetEnumerator();
                while (leftItems.MoveNext() && rightItems.MoveNext())
                {
                    if (!ValuesEqual(leftItems.Current, rightItems.Current, $"{path}[{index}]", depth + 1))
                    {
                        return false;
                    }

                    index++;
                }

                return true;
            }

            case JsonValueKind.Object:
            {
                var leftProperties = left.EnumerateObject().ToArray();
                if (leftProperties.Length != right.EnumerateObject().Count())
                {
                    return false;
                }

                foreach (var property in leftProperties)
                {
                    if (!right.TryGetProperty(property.Name, out var other)
                        || !ValuesEqual(property.Value, other, $"{path}.{property.Name}", depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            }

            default:
                return true;
        }
    }
}
