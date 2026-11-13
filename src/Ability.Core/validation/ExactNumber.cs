using System.Numerics;

namespace Ability.Core;

/// <summary>
/// 精确十进制数值结构：值 = <see cref="Significand"/> × 10^<see cref="Exponent"/>。
/// 使用任意精度大整数（BigInteger）保存有效数字，
/// 从而使大小比较、整数判定与倍数判定都不经过二进制浮点（double），杜绝浮点精度丢失。
/// </summary>
internal readonly struct ExactNumber
{
    /// <summary>
    /// 支持的十进制指数绝对值上限。
    /// </summary>
    public const int MaxExponentMagnitude = 1000;

    /// <summary>
    /// 初始化精确十进制数实例。
    /// </summary>
    public ExactNumber(BigInteger significand, int exponent)
    {
        Significand = significand;
        Exponent = exponent;
    }

    /// <summary>
    /// 有效数字（含正负号）。
    /// </summary>
    public BigInteger Significand { get; }

    /// <summary>
    /// 以 10 为底的指数。
    /// </summary>
    public int Exponent { get; }

    /// <summary>
    /// 数值是否精确为 0。
    /// </summary>
    public bool IsZero => Significand.IsZero;

    /// <summary>
    /// 判断数值是否为数学上的整数。
    /// 指数非负时必为整数；指数为负时要求有效数字能被 10^(-指数) 整除。
    /// </summary>
    public bool IsInteger =>
        Exponent >= 0 || Significand % BigInteger.Pow(10, -Exponent) == 0;

    /// <summary>
    /// 尝试将 JSON 原始数值文本解析为精确十进制数。
    /// 当指数绝对值超出 <see cref="MaxExponentMagnitude"/> 或格式非法时返回 false。
    /// </summary>
    /// <param name="text">JSON 数值文本。</param>
    /// <param name="number">解析得到的精确数值。</param>
    /// <returns>若成功解析返回 true，否则返回 false。</returns>
    public static bool TryParse(string text, out ExactNumber number)
    {
        number = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var span = text.AsSpan();
        var negative = span[0] == '-';
        if (negative)
        {
            span = span[1..];
        }

        var exponent = 0;
        var exponentIndex = span.IndexOfAny('e', 'E');
        if (exponentIndex >= 0)
        {
            if (!int.TryParse(span[(exponentIndex + 1)..], out exponent)
                || Math.Abs((long)exponent) > MaxExponentMagnitude)
            {
                return false;
            }

            span = span[..exponentIndex];
        }

        var digits = span.ToString().Replace(".", "", StringComparison.Ordinal);
        var fractionDigits = 0;
        var dotIndex = span.IndexOf('.');
        if (dotIndex >= 0)
        {
            fractionDigits = span.Length - dotIndex - 1;
        }

        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        exponent -= fractionDigits;
        if (Math.Abs((long)exponent) > MaxExponentMagnitude
            || !BigInteger.TryParse(digits, out var significand))
        {
            return false;
        }

        number = new ExactNumber(negative ? -significand : significand, exponent);
        return true;
    }

    /// <summary>
    /// 按精确大小比较两个数值。
    /// 将指数对齐至较小者后直接比较有效大整数，不使用任何浮点。
    /// </summary>
    public static int Compare(ExactNumber left, ExactNumber right)
    {
        var exponent = Math.Min(left.Exponent, right.Exponent);
        var leftValue = left.Significand * BigInteger.Pow(10, left.Exponent - exponent);
        var rightValue = right.Significand * BigInteger.Pow(10, right.Exponent - exponent);
        return leftValue.CompareTo(rightValue);
    }

    /// <summary>
    /// 判断 value 是否为 divisor 的整数倍。
    /// 全程使用指数对齐后的整数取余运算。
    /// </summary>
    public static bool IsMultipleOf(ExactNumber value, ExactNumber divisor)
    {
        if (divisor.IsZero)
        {
            return false;
        }

        var exponent = Math.Min(value.Exponent, divisor.Exponent);
        var scaledValue = value.Significand * BigInteger.Pow(10, value.Exponent - exponent);
        var scaledDivisor = divisor.Significand * BigInteger.Pow(10, divisor.Exponent - exponent);
        return scaledValue % scaledDivisor == 0;
    }
}
