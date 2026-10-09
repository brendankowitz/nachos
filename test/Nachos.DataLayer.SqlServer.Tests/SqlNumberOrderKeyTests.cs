using System.Globalization;
using System.Numerics;
using Microsoft.Data.SqlClient;
using Nachos.DataLayer.SqlServer.Storage;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// The numeric order key: <see cref="ExactDecimal.ToOrderKey"/> (C#, for filter operands) and the schema function
/// <c>dbo.JsonNumberOrderKey</c> (SQL, for stored numbers) must agree character for character, and keys must order
/// exactly like the numbers.
/// </summary>
[Collection(SqlServerDockerGroup.Name)]
public sealed class SqlNumberOrderKeyTests(SqlServerFixture fixture)
{
    /// <summary>Hand-picked spellings: equal values with different text, carries across 18 exponent digits, extremes.</summary>
    private static readonly string[] Edge =
    [
        "0", "-0", "0.0", "-0.000", "0e5", "0E-99999999999999999999", "1", "1.0", "1e0", "10e-1", "0.1e1", "100", "1e2", "1E+2",
        "1.5", "-1", "-1.5", "-1.55", "1.55", "1E400", "1e-400", "-1e-400", "-1E400", "5E-324", "4.9E-324", "1.7976931348623157E+308",
        "79228162514264337593543950335", "79228162514264337593543950336", "79228162514264337593543950337",
        "7.9228162514264337593543950336e28", "1234567890123456789012345678901234567890", "0.000123", "123e-6",
        "1e999999999999999999", "1e999999999999999998", "0.01e1000000000000000000", "123e999999999999999998",
        "1e1000000000000000000", "12345e999999999999999999999", "1.2345e1000000000000000000003",
        "123e-1000000000000000000000", "1.23e-999999999999999999998", "-1e999999999999999999999", "-1e-999999999999999999999",
        "999999999999999999e999999999999999999", "1e-1000000000000000017", "0.000000000000000000001e-999999999999999999997",
    ];

    /// <summary>Random literals in every JSON form, with exponents small enough for the exact rational oracle.</summary>
    private static IEnumerable<string> RandomLiterals(int count, int seed)
    {
        var random = new Random(seed);
        string Digits(int min, int max) =>
            string.Concat(Enumerable.Range(0, random.Next(min, max + 1)).Select(_ => (char)('0' + random.Next(10))));

        for (var i = 0; i < count; i++)
        {
            var integer = Digits(1, 25).TrimStart('0');
            if (integer.Length == 0 || random.Next(6) == 0)
            {
                integer = "0";
            }

            var text = (random.Next(2) == 0 ? "-" : string.Empty) + integer;
            if (random.Next(2) == 0)
            {
                text += "." + Digits(1, 25);
            }

            if (random.Next(2) == 0)
            {
                text += (random.Next(2) == 0 ? "e" : "E") + random.Next(3) switch { 0 => "-", 1 => "+", _ => string.Empty }
                    + random.Next(0, 40).ToString(CultureInfo.InvariantCulture);
            }

            yield return text;
        }
    }

    /// <summary>The exact value as numerator / 10^scale, parsed independently of <see cref="ExactDecimal"/>.</summary>
    private static (BigInteger Numerator, int Scale) Rational(string literal)
    {
        var e = literal.IndexOfAny(['e', 'E']);
        var significand = e < 0 ? literal : literal[..e];
        var exponent = e < 0 ? 0 : int.Parse(literal[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var dot = significand.IndexOf('.', StringComparison.Ordinal);
        var fraction = dot < 0 ? 0 : significand.Length - dot - 1;
        var numerator = BigInteger.Parse(significand.Replace(".", string.Empty, StringComparison.Ordinal), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var scale = fraction - exponent;
        return scale >= 0 ? (numerator, scale) : (numerator * BigInteger.Pow(10, -scale), 0);
    }

    private static int CompareExactly(string left, string right)
    {
        var (a, aScale) = Rational(left);
        var (b, bScale) = Rational(right);
        var scale = Math.Max(aScale, bScale);
        return (a * BigInteger.Pow(10, scale - aScale)).CompareTo(b * BigInteger.Pow(10, scale - bScale));
    }

    private static string Key(string literal) => ExactDecimal.Parse(literal).ToOrderKey();

    [Fact]
    public void Keys_OrderExactlyLikeTheValues()
    {
        var literals = RandomLiterals(400, seed: 7)
            .Concat(Edge.Where(e => !e.Contains("999999999999999", StringComparison.Ordinal) && !e.Contains("1000000000000000", StringComparison.Ordinal)))
            .ToList();

        foreach (var left in literals)
        {
            foreach (var right in literals.Take(120))
            {
                Math.Sign(string.CompareOrdinal(Key(left), Key(right))).ShouldBe(Math.Sign(CompareExactly(left, right)), $"{left} vs {right}");
            }
        }
    }

    [Fact]
    public void Keys_EqualExactlyForEqualValues_WhateverTheSpelling()
    {
        Key("1e2").ShouldBe(Key("100"));
        Key("100").ShouldBe(Key("100.000"));
        Key("-0").ShouldBe(Key("0e5"));
        Key("0.01e1000000000000000000").ShouldBe(Key("1e999999999999999998"));
        Key("12345e999999999999999999999").ShouldBe(Key("1.2345e1000000000000000000003"));
        Key("123e-1000000000000000000000").ShouldBe(Key("1.23e-999999999999999999998"));
        Key("79228162514264337593543950336").ShouldNotBe(Key("79228162514264337593543950337"));
        string.CompareOrdinal(Key("1e999999999999999999"), Key("1e1000000000000000000")).ShouldBeLessThan(0);
        string.CompareOrdinal(Key("-1e999999999999999999999"), Key("-1E400")).ShouldBeLessThan(0);
        string.CompareOrdinal(Key("-1e-999999999999999999999"), Key("0")).ShouldBeLessThan(0);
    }

    [Fact]
    public async Task SqlFunction_ProducesTheSameKeyAsCSharp()
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "order-key");
        var literals = Edge.Concat(RandomLiterals(300, seed: 11)).Append(new string('9', 5000) + "e-123456789012345678901234").ToList();

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT n.[key], dbo.JsonNumberOrderKey(n.[value]) FROM OPENJSON(@numbers) AS n ORDER BY CAST(n.[key] AS int)", connection);
        command.Parameters.Add("@numbers", System.Data.SqlDbType.NVarChar, -1).Value =
            $"[{string.Join(",", literals.Select(literal => $"\"{literal}\""))}]";

        await using var reader = await command.ExecuteReaderAsync();
        var checkedCount = 0;
        while (await reader.ReadAsync())
        {
            var literal = literals[int.Parse(reader.GetString(0), CultureInfo.InvariantCulture)];
            reader.GetString(1).ShouldBe(Key(literal), literal);
            checkedCount++;
        }

        checkedCount.ShouldBe(literals.Count);
    }

    [Fact]
    public async Task SqlFunction_OfNull_IsNull()
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "order-key");
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT dbo.JsonNumberOrderKey(NULL)", connection);
        (await command.ExecuteScalarAsync()).ShouldBe(DBNull.Value);
    }

    /// <summary>Text that is not number-shaped gets a NULL key from both functions, never an error.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("e5")]
    [InlineData("-")]
    [InlineData("abc")]
    [InlineData("1e5x")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("1.2.3")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("--1")]
    [InlineData("1e5e5")]
    [InlineData("0x10")]
    [InlineData("١")]
    [InlineData("１")]                 // fullwidth digit one: a varchar CAST under a linguistic collation maps it to 1
    [InlineData("１e2")]
    [InlineData("-１")]
    [InlineData("1２")]
    [InlineData("१")]                  // Devanagari one
    [InlineData("৫")]                  // Bengali five
    [InlineData("²")]                  // superscripts
    [InlineData("³1")]
    [InlineData("¹")]
    [InlineData("1,5")]
    [InlineData("1/2")]
    [InlineData("𝟙")]                  // mathematical double-struck one (supplementary)
    public async Task SqlFunctions_OfTextThatIsNotANumber_AreNull(string text)
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "order-key");
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT dbo.JsonNumberOrderKey(@text), dbo.JsonNumberOrderKeyLong(@text)", connection);
        command.Parameters.Add("@text", System.Data.SqlDbType.NVarChar, -1).Value = text;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.IsDBNull(0).ShouldBeTrue("JsonNumberOrderKey");
        reader.IsDBNull(1).ShouldBeTrue("JsonNumberOrderKeyLong");
    }

    /// <summary>Text of more than 4000 characters is delegated to the long form, at the boundary too.</summary>
    [Fact]
    public async Task SqlFunctions_AgreeAcrossTheLengthBoundary()
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "order-key");
        string[] literals = [new string('9', 4000), new string('9', 4001), "-0." + new string('0', 3995) + "1", "1." + new string('5', 3998) + "e-12345678901234567890"];
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        foreach (var literal in literals)
        {
            await using var command = new SqlCommand("SELECT dbo.JsonNumberOrderKey(@text), dbo.JsonNumberOrderKeyLong(@text)", connection);
            command.Parameters.Add("@text", System.Data.SqlDbType.NVarChar, -1).Value = literal;
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();
            reader.GetString(0).ShouldBe(Key(literal), $"length {literal.Length}");
            reader.GetString(1).ShouldBe(Key(literal), $"length {literal.Length}");
        }
    }

    /// <summary>
    /// The two schema functions are one algorithm: their bodies must be identical apart from <c>VARCHAR (8000)</c> versus
    /// <c>VARCHAR (MAX)</c> and the delegation at the top of the fast one.
    /// </summary>
    [Fact]
    public void SchemaFunctions_ShareOneBody()
    {
        var directory = Path.Combine(RepoRoot(), "src", "DataLayer", "Nachos.DataLayer.SqlServer.Database", "Functions");
        static string Body(string text) => text[(text.IndexOf("    -- Not number-shaped", StringComparison.Ordinal))..];
        var fast = Body(File.ReadAllText(Path.Combine(directory, "JsonNumberOrderKey.sql")));
        var general = Body(File.ReadAllText(Path.Combine(directory, "JsonNumberOrderKeyLong.sql")));

        fast.ShouldNotContain("VARCHAR (MAX)");
        fast.Replace("VARCHAR (8000)", "VARCHAR (MAX)", StringComparison.Ordinal).ShouldBe(general);
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Nachos.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Nachos.slnx was not found above the test output directory.");
    }
}
