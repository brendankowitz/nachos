using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Nachos.Core.Keys;

/// <summary>Exact JWT NumericDate ordering; DateTimeOffset projection is deliberately separate.</summary>
internal sealed class NumericDate
{
    private static readonly long MinimumTicks = (DateTimeOffset.MinValue - DateTimeOffset.UnixEpoch).Ticks;
    private static readonly long MaximumTicks = (DateTimeOffset.MaxValue - DateTimeOffset.UnixEpoch).Ticks;
    private readonly int _sign;
    private readonly string _digits;
    private readonly BigInteger _exponent;

    private NumericDate(int sign, string digits, BigInteger exponent)
    {
        _sign = sign;
        _digits = digits;
        _exponent = exponent;
    }

    public static NumericDate Parse(JsonElement value)
    {
        // JsonElement already guarantees number syntax. Keep significant digits and the exponent,
        // never a rounded decimal/double or a coefficient multiplied by an expanded power.
        var text = value.GetRawText().AsSpan();
        var negative = text[0] == '-';
        if (negative)
        {
            text = text[1..];
        }

        var exponentIndex = text.IndexOfAny('e', 'E');
        var mantissa = exponentIndex < 0 ? text : text[..exponentIndex];
        var point = mantissa.IndexOf('.');
        var integerLength = point < 0 ? mantissa.Length : point;
        var digits = point < 0
            ? mantissa.ToString()
            : string.Concat(mantissa[..point], mantissa[(point + 1)..]);
        var first = digits.AsSpan().IndexOfAnyExcept('0');
        if (first < 0)
        {
            return new NumericDate(0, string.Empty, BigInteger.Zero);
        }

        var last = digits.AsSpan().LastIndexOfAnyExcept('0');
        var exponent = exponentIndex < 0
            ? BigInteger.Zero
            : BigInteger.Parse(text[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var date = new NumericDate(negative ? -1 : 1, digits[first..(last + 1)],
            exponent + integerLength - 1 - first);
        if (date.CompareToUnixTicks(MinimumTicks) < 0 || date.CompareToUnixTicks(MaximumTicks) > 0)
        {
            throw new FormatException("NumericDate is outside the supported DateTimeOffset range.");
        }

        return date;
    }

    public int CompareToUnixTicks(long ticks)
    {
        var sign = Math.Sign(ticks);
        if (_sign != sign)
        {
            return _sign.CompareTo(sign);
        }

        if (_sign == 0)
        {
            return 0;
        }

        var digits = Math.Abs(ticks).ToString(CultureInfo.InvariantCulture);
        // Unix ticks have seven decimal places. Compare leading-digit powers, then the
        // normalized significant digits; no leading/trailing zero can change lexical order.
        var magnitude = _exponent.CompareTo(digits.Length - 8);
        if (magnitude == 0)
        {
            magnitude = string.CompareOrdinal(_digits, digits.TrimEnd('0'));
        }

        return _sign * Math.Sign(magnitude);
    }

    public DateTimeOffset ToDateTimeOffset()
    {
        if (_sign == 0 || _exponent < -7)
        {
            return DateTimeOffset.UnixEpoch;
        }

        // Range validation bounds this to at most 19 digits. Only DTO projection discards
        // sub-tick precision, truncating toward zero as the existing contract did.
        var count = (int)_exponent + 8;
        long ticks = 0;
        for (var index = 0; index < count; index++)
        {
            ticks = ticks * 10 + (index < _digits.Length ? _digits[index] - '0' : 0);
        }

        return DateTimeOffset.UnixEpoch.AddTicks(_sign * ticks);
    }
}
