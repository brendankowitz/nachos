using System.Globalization;
using System.Numerics;

namespace Nachos.DataLayer.InMemory;

/// <summary>Exact comparison of JSON number literals by numeric value, with no precision or range bound.</summary>
/// <remarks>
/// A literal is reduced to its sign, its significant digits (no leading or trailing zeros) and the base-10 exponent of
/// its first significant digit; values then compare by sign, then exponent, then digits. So <c>1</c>, <c>1.0</c>,
/// <c>1e0</c>, <c>10e-1</c> and <c>0.1e1</c> are one value, and every zero (<c>0</c>, <c>-0</c>, <c>0.0</c>,
/// <c>0e5</c>) is another. Exponents are held as <see cref="BigInteger"/> and never expanded, so a literal such as
/// <c>1e999999999999999999999</c> costs only its length.
/// </remarks>
internal static class JsonNumberComparison
{
    /// <summary>Compares two JSON number literals by value: negative, zero or positive as left is less, equal or greater.</summary>
    /// <exception cref="FormatException">A literal is not JSON number text.</exception>
    public static int Compare(string left, string right)
    {
        var x = Parse(left);
        var y = Parse(right);
        if (x.Sign != y.Sign)
        {
            return x.Sign.CompareTo(y.Sign);
        }

        if (x.Sign == 0)
        {
            return 0;
        }

        // Digits have no leading or trailing zeros, so with equal exponents ordinal order is magnitude order.
        var magnitude = x.Exponent != y.Exponent
            ? x.Exponent.CompareTo(y.Exponent)
            : Math.Sign(string.CompareOrdinal(x.Digits, y.Digits));
        return x.Sign * magnitude;
    }

    /// <summary>
    /// A nonzero value is <c>Sign × 0.d₁d₂… × 10^(Exponent + 1)</c>, that is <c>d₁</c> has weight
    /// <c>10^Exponent</c>. Zero is <c>(0, "", 0)</c>.
    /// </summary>
    private readonly record struct Number(int Sign, string Digits, BigInteger Exponent);

    /// <summary>Parses <c>-? digits (. digits)? ([eE] [+-]? digits)?</c>.</summary>
    private static Number Parse(string text)
    {
        var rest = text.AsSpan();
        var negative = TrySkip(ref rest, '-');

        var integer = TakeDigits(ref rest);
        var fraction = TrySkip(ref rest, '.') ? TakeDigits(ref rest) : [];

        var exponent = BigInteger.Zero;
        if (TrySkip(ref rest, 'e') || TrySkip(ref rest, 'E'))
        {
            var negativeExponent = TrySkip(ref rest, '-');
            if (!negativeExponent)
            {
                TrySkip(ref rest, '+');
            }

            exponent = BigInteger.Parse(TakeDigits(ref rest), NumberStyles.None, CultureInfo.InvariantCulture);
            if (negativeExponent)
            {
                exponent = -exponent;
            }
        }

        if (!rest.IsEmpty)
        {
            throw NotANumber();
        }

        var digits = string.Concat(integer, fraction);
        var first = digits.AsSpan().IndexOfAnyExcept('0');
        if (first < 0)
        {
            return new Number(0, string.Empty, BigInteger.Zero);
        }

        var last = digits.AsSpan().LastIndexOfAnyExcept('0');

        // The decimal point follows the integer digits, so digit i has weight 10^(integer.Length - 1 - i + exponent).
        return new Number(negative ? -1 : 1, digits[first..(last + 1)], exponent + (integer.Length - 1 - first));
    }

    private static bool TrySkip(ref ReadOnlySpan<char> rest, char expected)
    {
        if (rest.IsEmpty || rest[0] != expected)
        {
            return false;
        }

        rest = rest[1..];
        return true;
    }

    /// <summary>Takes one or more ASCII digits from the front of <paramref name="rest"/>.</summary>
    private static ReadOnlySpan<char> TakeDigits(ref ReadOnlySpan<char> rest)
    {
        var length = rest.IndexOfAnyExceptInRange('0', '9');
        if (length < 0)
        {
            length = rest.Length;
        }

        if (length == 0)
        {
            throw NotANumber();
        }

        var digits = rest[..length];
        rest = rest[length..];
        return digits;
    }

    // The message leaves the text out: it may be stored metadata.
    private static FormatException NotANumber() => new("The text is not a JSON number.");
}
