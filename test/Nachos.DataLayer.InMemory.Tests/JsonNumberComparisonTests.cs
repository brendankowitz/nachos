using System.Diagnostics;
using Shouldly;

namespace Nachos.DataLayer.InMemory.Tests;

/// <summary>
/// Exact comparison of JSON number literals: no precision or range bound, every zero is one value, and huge exponents
/// are never expanded.
/// </summary>
public sealed class JsonNumberComparisonTests
{
    private const string Huge = "1e999999999999999999999";
    private const string Tiny = "1e-999999999999999999999";
    private const string DecimalMaxPlusOne = "79228162514264337593543950336";
    private const string DecimalMaxPlusTwo = "79228162514264337593543950337";

    /// <summary>Pairs with the expected sign of <c>Compare(left, right)</c>.</summary>
    public static TheoryData<string, string, int> Ordered() => new()
    {
        // Below decimal's smallest step and below double's range: still not zero.
        { "0", "1e-29", -1 },
        { "0", "1e-400", -1 },
        { "1e-401", "1e-400", -1 },
        { "-1e-400", "0", -1 },
        { "0", Tiny, -1 },

        // Every zero is one value, whatever its sign or spelling.
        { "-0", "0", 0 },
        { "-0.0", "0e5", 0 },
        { "0.000", "-0E-7", 0 },

        // Distinct integers beyond decimal's range and double's precision.
        { DecimalMaxPlusOne, DecimalMaxPlusTwo, -1 },
        { "-" + DecimalMaxPlusOne, "-" + DecimalMaxPlusTwo, 1 },

        // One value, many spellings.
        { "1", "1.0", 0 },
        { "1", "1e0", 0 },
        { "1", "10e-1", 0 },
        { "1", "0.1e1", 0 },
        { "1.0", "1E+0", 0 },
        { "100e-2", "0.01e2", 0 },
        { "12300", "1.23e4", 0 },

        // Beyond double's range, ordered by exponent then digits.
        { "1e400", "1e401", -1 },
        { "-1e400", "-1e401", 1 },
        { "2e400", "1e401", -1 },
        { "1.5e400", "1.25e400", 1 },

        // Magnitude is decided by the exponent before the digits.
        { "2", "10", -1 },
        { "-2", "-10", 1 },
        { "12", "123", -1 },
        { "0.5", "0.25", 1 },

        // Sign decides across signs.
        { "-1", "1", -1 },
        { "-5", "0", -1 },
        { "-1e400", "1e-400", -1 },
        { "-0.5", "-0.25", -1 },

        // Beyond decimal's precision in the fraction.
        { "0.1", "0.10000000000000000000000000001", -1 },
        { "0.10000000000000000000000000000", "0.1", 0 },

        // Huge exponents: compared, never expanded.
        { Huge, "1", 1 },
        { "-" + Huge, "1", -1 },
        { Huge, "1e999999999999999999998", 1 },
        { Huge, "10e999999999999999999998", 0 },
        { "-" + Huge, "-1e999999999999999999998", -1 },
        { Tiny, "1e-999999999999999999998", -1 },
    };

    [Theory]
    [MemberData(nameof(Ordered))]
    public void Compare_OrdersByExactValue_AndIsAntisymmetric(string left, string right, int expected)
    {
        Math.Sign(JsonNumberComparison.Compare(left, right)).ShouldBe(expected);
        Math.Sign(JsonNumberComparison.Compare(right, left)).ShouldBe(-expected);
        JsonNumberComparison.Compare(left, left).ShouldBe(0);
    }

    [Fact]
    public void Compare_WithAnExponentOfManyDigits_IsQuick()
    {
        var hugeExponent = "1e" + new string('9', 100_000);
        var stopwatch = Stopwatch.StartNew();

        Math.Sign(JsonNumberComparison.Compare(hugeExponent, Huge)).ShouldBe(1);
        Math.Sign(JsonNumberComparison.Compare("-" + hugeExponent, "-" + Huge)).ShouldBe(-1);

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("abc")]
    [InlineData("1 ")]
    [InlineData("\"1\"")]
    public void Compare_RejectsTextThatIsNotANumber(string text)
    {
        Should.Throw<FormatException>(() => JsonNumberComparison.Compare(text, "1"));
        Should.Throw<FormatException>(() => JsonNumberComparison.Compare("1", text));
    }
}
