using System.Globalization;
using System.Numerics;
using System.Text;

namespace Nachos.DataLayer.SqlServer.Storage;

/// <summary>
/// The exact value of a JSON number literal, and its order key: the form in which SQL Server compares numbers in
/// filters. Nothing here bounds a number's size or precision.
/// </summary>
/// <remarks>
/// A nonzero value is <c>Sign × d₁.d₂d₃… × 10^Exponent</c>: <see cref="Digits"/> has no leading or trailing zeros and
/// <see cref="Exponent"/> is the power of ten of its first digit. Zero is <c>(0, "", 0)</c>. This is the same reduction
/// the in-memory provider's <c>JsonNumberComparison</c> uses, so both providers order numbers identically. Exponents are
/// <see cref="BigInteger"/>, so a literal such as <c>1e999999999999999999999</c> costs only its length.
/// </remarks>
internal readonly record struct ExactDecimal(int Sign, string Digits, BigInteger Exponent)
{
    /// <summary>The width of the digit count of the exponent in a key; it bounds nothing a SQL string can hold.</summary>
    private const int CountWidth = 10;

    /// <summary>Parses JSON number text: <c>-? digits (. digits)? ([eE] [+-]? digits)?</c>.</summary>
    /// <exception cref="FormatException">The text is not a JSON number.</exception>
    public static ExactDecimal Parse(string literal)
    {
        var rest = literal.AsSpan();
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
            return new ExactDecimal(0, string.Empty, BigInteger.Zero);
        }

        var last = digits.AsSpan().LastIndexOfAnyExcept('0');
        return new ExactDecimal(negative ? -1 : 1, digits[first..(last + 1)], exponent + (integer.Length - 1 - first));
    }

    /// <summary>
    /// The order key: two numbers compare exactly like their keys compared ordinally (as SQL Server compares them under
    /// a binary collation, where a proper prefix sorts first). This is the algorithm of the schema function
    /// <c>dbo.JsonNumberOrderKey</c>, which derives the key of a stored number in SQL; the two must change together.
    /// </summary>
    /// <remarks>
    /// Zero is <c>1</c>. Otherwise, with <c>F</c> the digit count of <c>|Exponent|</c> in ten digits followed by the
    /// digits of <c>|Exponent|</c>, the exponent field is <c>1</c> + <c>F</c> for a non-negative exponent and
    /// <c>0</c> + 9-complement(<c>F</c>) for a negative one. A positive number is <c>2</c> + field + <see cref="Digits"/>;
    /// a negative one is <c>0</c> + 9-complement(field + <see cref="Digits"/>) + <c>:</c>, where <c>:</c> sorts after
    /// every digit, so the larger magnitude sorts first even when one mantissa is a prefix of the other.
    /// </remarks>
    public string ToOrderKey()
    {
        if (Sign == 0)
        {
            return "1";
        }

        var magnitude = BigInteger.Abs(Exponent).ToString(CultureInfo.InvariantCulture);
        var count = magnitude.Length.ToString(CultureInfo.InvariantCulture).PadLeft(CountWidth, '0');
        var field = Exponent.Sign >= 0 ? "1" + count + magnitude : "0" + Complement(count + magnitude);
        return Sign > 0 ? "2" + field + Digits : "0" + Complement(field + Digits) + ":";
    }

    private static string Complement(string digits)
    {
        var complemented = new StringBuilder(digits.Length);
        foreach (var digit in digits)
        {
            complemented.Append((char)('9' - digit + '0'));
        }

        return complemented.ToString();
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
