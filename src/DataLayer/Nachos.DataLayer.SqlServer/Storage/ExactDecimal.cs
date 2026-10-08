using System.Globalization;
using System.Numerics;
using System.Text;

namespace Nachos.DataLayer.SqlServer.Storage;

/// <summary>
/// The exact value of a JSON number literal, and the two decimal forms SQL Server needs: the plain text a <c>json</c>
/// column stores without loss, and an order key that compares like the value.
/// </summary>
/// <remarks>
/// A nonzero value is <c>Sign × d₁.d₂d₃… × 10^Exponent</c>: <see cref="Digits"/> has no leading or trailing zeros and
/// <see cref="Exponent"/> is the power of ten of its first digit. Zero is <c>(0, "", 0)</c>. Exponents are
/// <see cref="BigInteger"/> and never expanded, so <c>1e999999999999</c> costs only its length.
/// </remarks>
internal readonly record struct ExactDecimal(int Sign, string Digits, BigInteger Exponent)
{
    /// <summary>
    /// The most significant digits a <c>json</c> column keeps exactly: it stores a number as a SQL <c>decimal</c>, whose
    /// precision is at most 38. Precision counts the integer digits (none for a lone <c>0</c>) plus every fraction digit.
    /// </summary>
    public const int MaxPrecision = 38;

    /// <summary>The order key's digits on each side of the decimal point: weights 10^37 down to 10^-38.</summary>
    private const int KeyDigits = 2 * MaxPrecision;

    private static readonly string OverflowMagnitude = new('9', KeyDigits + 1);

    /// <summary>Parses JSON number text: <c>-? digits (. digits)? ([eE] [+-]? digits)?</c>.</summary>
    /// <exception cref="FormatException">The text is not a JSON number.</exception>
    public static ExactDecimal Parse(string literal)
    {
        var (negative, integer, fraction, exponent) = Split(literal);
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
    /// True when <paramref name="literal"/> is plain JSON number text (no exponent) that a <c>json</c> column stores
    /// exactly as written: at most <see cref="MaxPrecision"/> significant positions.
    /// </summary>
    public static bool IsStoredVerbatim(string literal)
    {
        var (_, integer, fraction, exponent) = Split(literal);
        return literal.AsSpan().IndexOfAny('e', 'E') < 0
            && exponent.IsZero
            && (integer is "0" ? 0 : integer.Length) + fraction.Length <= MaxPrecision;
    }

    /// <summary>
    /// The plain decimal text of the value with no redundant zeros (<c>1e2</c> is <c>100</c>, <c>2.5E-1</c> is
    /// <c>0.25</c>, every zero is <c>0</c>), or null when it needs more than <see cref="MaxPrecision"/> positions, so a
    /// <c>json</c> column cannot hold it exactly.
    /// </summary>
    public string? ToPlain()
    {
        if (Sign == 0)
        {
            return "0";
        }

        var lowest = Exponent - (Digits.Length - 1);
        var integerDigits = Exponent >= 0 ? Exponent + 1 : BigInteger.Zero;
        var fractionDigits = lowest < 0 ? -lowest : BigInteger.Zero;
        if (integerDigits + fractionDigits > MaxPrecision)
        {
            return null;
        }

        var high = (int)BigInteger.Max(Exponent, 0);
        var low = (int)BigInteger.Min(lowest, 0);
        var text = new StringBuilder(Sign < 0 ? "-" : string.Empty);
        for (var weight = high; weight >= 0; weight--)
        {
            text.Append(DigitAt(weight));
        }

        if (low < 0)
        {
            text.Append('.');
            for (var weight = -1; weight >= low; weight--)
            {
                text.Append(DigitAt(weight));
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// An order key: compare signs first; for equal nonzero signs, compare the magnitudes ordinally (reversed when
    /// negative). The magnitude is 76 digits for weights 10^37 down to 10^-38, matching what SQL derives from a stored
    /// value. A value beyond that window gets a 77-character magnitude that orders correctly against every 76-digit one
    /// and equals none: 77 nines above 10^38, or the truncated digits followed by <c>5</c> for digits below 10^-38.
    /// </summary>
    public (int Sign, string Magnitude) ToOrderKey()
    {
        if (Sign == 0)
        {
            return (0, new string('0', KeyDigits));
        }

        if (Exponent >= MaxPrecision)
        {
            return (Sign, OverflowMagnitude);
        }

        var key = new char[KeyDigits];
        Array.Fill(key, '0');
        var truncated = false;
        for (var i = 0; i < Digits.Length; i++)
        {
            var weight = Exponent - i;
            if (weight < -MaxPrecision)
            {
                truncated = true;
                break;
            }

            key[(int)(MaxPrecision - 1 - weight)] = Digits[i];
        }

        return (Sign, truncated ? new string(key) + "5" : new string(key));
    }

    private char DigitAt(int weight)
    {
        var index = Exponent - weight;
        return index >= 0 && index < Digits.Length ? Digits[(int)index] : '0';
    }

    private static (bool Negative, string Integer, string Fraction, BigInteger Exponent) Split(string literal)
    {
        var rest = literal.AsSpan();
        var negative = TrySkip(ref rest, '-');
        var integer = TakeDigits(ref rest).ToString();
        var fraction = TrySkip(ref rest, '.') ? TakeDigits(ref rest).ToString() : string.Empty;

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

        return rest.IsEmpty ? (negative, integer, fraction, exponent) : throw NotANumber();
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
