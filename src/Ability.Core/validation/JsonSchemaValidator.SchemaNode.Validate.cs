using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ability.Core;

/// <summary>
/// Schema 语法树的实例校验分部：按已解析的不可变规则判定实例是否合规，并累积带路径的违规项。
/// 解析与规则构建见 JsonSchemaValidator.SchemaNode.cs。
/// </summary>
public sealed partial class JsonSchemaValidator
{
    /// <summary>
    /// Schema 内部语法树节点的实例校验步骤。
    /// </summary>
    private sealed partial class SchemaNode
    {
        /// <summary>
        /// 校验给定的实例数据是否满足当前节点定义的约束。
        /// </summary>
        /// <param name="instance">待校验的 JSON 实例元素。</param>
        /// <param name="path">当前实例路径。</param>
        /// <param name="violations">累积违规项列表。</param>
        /// <param name="depth">当前递归深度。</param>
        internal void Validate(JsonElement instance, string path, List<SchemaViolation> violations, int depth)
        {
            if (depth > MaxSchemaDepth)
            {
                throw new SchemaValidationIncompleteException("depth", path, $"实例嵌套深度超过允许上限 {MaxSchemaDepth}。");
            }

            if (_rejectsEverything)
            {
                violations.Add(new SchemaViolation(path, "false", "该位置要求拒绝任何值。"));
                return;
            }

            if (_allowsAnything)
            {
                return;
            }

            if (_const is JsonElement expected && !ValuesEqual(instance, expected, path, depth))
            {
                violations.Add(new SchemaViolation(path, "const", "值与声明的常量不一致。"));
            }

            if (_enum is { } allowed && !allowed.Any(candidate => ValuesEqual(instance, candidate, path, depth)))
            {
                violations.Add(new SchemaViolation(path, "enum", "值不在允许集合内。"));
            }

            if (_types.Count > 0 && !_types.Any(type => MatchesType(instance, type, path)))
            {
                violations.Add(new SchemaViolation(path, "type", $"实际为 {Describe(instance)}，要求 {string.Join('/', _types)}。"));
            }

            if (instance.ValueKind == JsonValueKind.Number)
            {
                ValidateNumber(instance, path, violations);
            }

            if (instance.ValueKind == JsonValueKind.String)
            {
                ValidateString(instance, path, violations);
            }

            if (instance.ValueKind == JsonValueKind.Array)
            {
                ValidateArray(instance, path, violations, depth);
            }

            if (instance.ValueKind == JsonValueKind.Object)
            {
                ValidateObject(instance, path, violations, depth);
            }
        }

        /// <summary>
        /// 校验数值实例是否满足数值范围、开闭区间和倍数约束。
        /// </summary>
        /// <param name="instance">数值类型的 JSON 实例元素。</param>
        /// <param name="path">当前实例路径。</param>
        /// <param name="violations">累积违规项列表。</param>
        private void ValidateNumber(JsonElement instance, string path, List<SchemaViolation> violations)
        {
            if (!TryParseNumber(instance.GetRawText(), out var value))
            {
                throw new SchemaValidationIncompleteException(
                    "number",
                    path,
                    $"数值超出可求值范围（十进制指数绝对值不超过 {ExactNumber.MaxExponentMagnitude}）。");
            }

            if (_minimum is ExactNumber minimum && ExactNumber.Compare(value, minimum) < 0)
            {
                violations.Add(new SchemaViolation(path, "minimum", "低于允许的最小值。"));
            }

            if (_maximum is ExactNumber maximum && ExactNumber.Compare(value, maximum) > 0)
            {
                violations.Add(new SchemaViolation(path, "maximum", "高于允许的最大值。"));
            }

            if (_exclusiveMinimum is ExactNumber exclusiveMinimum && ExactNumber.Compare(value, exclusiveMinimum) <= 0)
            {
                violations.Add(new SchemaViolation(path, "exclusiveMinimum", "未大于允许的下限（开区间）。"));
            }

            if (_exclusiveMaximum is ExactNumber exclusiveMaximum && ExactNumber.Compare(value, exclusiveMaximum) >= 0)
            {
                violations.Add(new SchemaViolation(path, "exclusiveMaximum", "未小于允许的上限（开区间）。"));
            }

            if (_multipleOf is ExactNumber multipleOf && !ExactNumber.IsMultipleOf(value, multipleOf))
            {
                violations.Add(new SchemaViolation(path, "multipleOf", "不是声明倍数的整数倍。"));
            }
        }

        /// <summary>
        /// 校验字符串实例是否满足长度范围和正则表达式约束。
        /// </summary>
        /// <param name="instance">字符串类型的 JSON 实例元素。</param>
        /// <param name="path">当前实例路径。</param>
        /// <param name="violations">累积违规项列表。</param>
        private void ValidateString(JsonElement instance, string path, List<SchemaViolation> violations)
        {
            var value = instance.GetString() ?? string.Empty;
            var length = 0;
            foreach (var _ in value.EnumerateRunes())
            {
                length++;
            }

            if (_minLength is int minLength && length < minLength)
            {
                violations.Add(new SchemaViolation(path, "minLength", "长度少于声明的下限。"));
            }

            if (_maxLength is int maxLength && length > maxLength)
            {
                violations.Add(new SchemaViolation(path, "maxLength", "长度超过声明的上限。"));
            }

            if (_pattern is Regex pattern)
            {
                try
                {
                    if (!pattern.IsMatch(value))
                    {
                        violations.Add(new SchemaViolation(path, "pattern", "不匹配声明的模式。"));
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    throw new SchemaValidationIncompleteException("pattern", path);
                }
            }
        }

        /// <summary>
        /// 校验数组实例是否满足元素数量范围以及元素项 Schema 约束。
        /// </summary>
        /// <param name="instance">数组类型的 JSON 实例元素。</param>
        /// <param name="path">当前实例路径。</param>
        /// <param name="violations">累积违规项列表。</param>
        /// <param name="depth">当前递归深度。</param>
        private void ValidateArray(JsonElement instance, string path, List<SchemaViolation> violations, int depth)
        {
            var count = instance.GetArrayLength();
            if (_minItems is int minItems && count < minItems)
            {
                violations.Add(new SchemaViolation(path, "minItems", "元素数少于声明的下限。"));
            }

            if (_maxItems is int maxItems && count > maxItems)
            {
                violations.Add(new SchemaViolation(path, "maxItems", "元素数超过声明的上限。"));
            }

            if (_items is not null)
            {
                var index = 0;
                foreach (var item in instance.EnumerateArray())
                {
                    _items.Validate(item, $"{path}[{index}]", violations, depth + 1);
                    index++;
                }
            }
        }

        /// <summary>
        /// 校验对象实例是否包含必填属性，并递归校验属性与附加属性。
        /// </summary>
        /// <param name="instance">对象类型的 JSON 实例元素。</param>
        /// <param name="path">当前实例路径。</param>
        /// <param name="violations">累积违规项列表。</param>
        /// <param name="depth">当前递归深度。</param>
        private void ValidateObject(JsonElement instance, string path, List<SchemaViolation> violations, int depth)
        {
            foreach (var name in _required ?? [])
            {
                if (!instance.TryGetProperty(name, out _))
                {
                    violations.Add(new SchemaViolation(path, "required", $"缺少必填字段 {name}。"));
                }
            }

            foreach (var property in instance.EnumerateObject())
            {
                if (_properties is not null && _properties.TryGetValue(property.Name, out var schema))
                {
                    schema.Validate(property.Value, $"{path}.{property.Name}", violations, depth + 1);
                    continue;
                }

                if (_additionalPropertiesSchema is not null)
                {
                    _additionalPropertiesSchema.Validate(property.Value, $"{path}.{property.Name}", violations, depth + 1);
                }
                else if (_additionalPropertiesAllowed == false)
                {
                    violations.Add(new SchemaViolation(path, "additionalProperties", "包含未声明的字段。"));
                }
            }
        }

        /// <summary>
        /// 判定 JSON 实例是否匹配指定的 JSON 类型名称（含 integer 的整型数判定）。
        /// </summary>
        /// <param name="instance">JSON 实例元素。</param>
        /// <param name="type">期望的类型名称。</param>
        /// <param name="path">当前实例路径。</param>
        /// <returns>类型匹配返回 true；否则返回 false。</returns>
        private static bool MatchesType(JsonElement instance, string type, string path)
        {
            switch (type)
            {
                case "object":
                    return instance.ValueKind == JsonValueKind.Object;
                case "array":
                    return instance.ValueKind == JsonValueKind.Array;
                case "string":
                    return instance.ValueKind == JsonValueKind.String;
                case "number":
                    return instance.ValueKind == JsonValueKind.Number;
                case "integer":
                    if (instance.ValueKind != JsonValueKind.Number)
                    {
                        return false;
                    }

                    if (!TryParseNumber(instance.GetRawText(), out var number))
                    {
                        throw new SchemaValidationIncompleteException(
                            "type",
                            path,
                            $"数值超出可求值范围（十进制指数绝对值不超过 {ExactNumber.MaxExponentMagnitude}）。");
                    }

                    return number.IsInteger;
                case "boolean":
                    return instance.ValueKind is JsonValueKind.True or JsonValueKind.False;
                case "null":
                    return instance.ValueKind == JsonValueKind.Null;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 获取 JSON 实例的易读类型名称字符串描述。
        /// </summary>
        /// <param name="instance">JSON 实例元素。</param>
        /// <returns>类型描述字符串。</returns>
        private static string Describe(JsonElement instance)
            => instance.ValueKind switch
            {
                JsonValueKind.Object => "object",
                JsonValueKind.Array => "array",
                JsonValueKind.String => "string",
                JsonValueKind.Number => "number",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                JsonValueKind.Null => "null",
                _ => instance.ValueKind.ToString()
            };
    }
}
