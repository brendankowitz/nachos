using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Nachos.DataLayer.SqlServer.Filtering;
using Nachos.DataLayer.SqlServer.Storage;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// The packed entries <see cref="SqlDigest"/> computes in .NET are byte-identical to what SQL Server computes, with the
/// provider's own SQL expressions, for the same stored value read the way filters read it (<c>OPENJSON</c> of the stored
/// JSON): <c>len:SHA256</c> of a string (code-unit count, UTF-16LE code units), the hex of a short string's code units,
/// and <c>len:SHA256</c> of a number's order key.
/// </summary>
[Collection(SqlServerDockerGroup.Name)]
public sealed class SqlDigestTests(SqlServerFixture fixture)
{
    private static IEnumerable<string> SweepStrings()
    {
        string[] fixedStrings =
        [
            "", " ", "  ", "a", "a ", "a  ", " a", "\u0000", "a\u0000b", "\u0000\u0000", "|", "||", "a|b", "\t\r\n", "😀", "a😀",
            "😀😀", "\uD83D\uDE00 ", "\uFFFF", "\uFFFE", "\uE000", "é", "e\u0301", "Ա", "中文", "\u00A0", "\u2028", "'", "\"", "\\",
            "\u007F", "\u0080",
        ];
        foreach (var value in fixedStrings)
        {
            yield return value;
        }

        // Every 97th BMP code unit outside the surrogate range, alone and after a prefix.
        for (var unit = 1; unit <= 0xFFFF; unit += 97)
        {
            if (unit is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            yield return ((char)unit).ToString();
            yield return "ab" + (char)unit;
        }

        // Lengths around the raw/digest limit (16 units), with a surrogate pair at the edge, and long strings.
        foreach (var length in new[] { 15, 16, 17, 1990, 1999, 2000, 2010, 4000, 4001, 8000, 10_000 })
        {
            yield return new string('x', length);
            yield return new string('x', length - 2) + "😀";
            yield return new string(' ', length);
        }
    }

    [Fact]
    public async Task StringDigestsAndHex_MatchSqlServer()
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "digest");
        var strings = SweepStrings().ToList();
        var json = new JsonArray([.. strings.Select(s => (JsonNode)s)]).ToJsonString();

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"""
            SELECT CAST(j.[key] AS int),
                   {SqlDigest.StringSql("j.[value]")},
                   CASE WHEN DATALENGTH(j.[value]) <= 32 THEN CONVERT(varchar(64), CAST(j.[value] AS varbinary(32)), 2) END
            FROM OPENJSON(@json) AS j
            """,
            connection);
        command.Parameters.Add("@json", System.Data.SqlDbType.NVarChar, -1).Value = json;

        var checkedCount = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var value = strings[reader.GetInt32(0)];
            reader.GetString(1).ShouldBe(SqlDigest.OfString(value), $"entry of string {reader.GetInt32(0)} (length {value.Length})");
            reader.GetString(1).ShouldStartWith(value.Length.ToString(CultureInfo.InvariantCulture) + ":");
            if (value.Length <= 16)
            {
                reader.GetString(2).ShouldBe(SqlDigest.Utf16Hex(value), $"hex of string {reader.GetInt32(0)}");
            }

            checkedCount++;
        }

        checkedCount.ShouldBe(strings.Count);
    }

    [Fact]
    public async Task KeyDigests_MatchSqlServer()
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "digest");
        string[] numbers =
        [
            "0", "-0", "1", "-1", "1e2", "100", "1.5E+3", "-1e-400", "5E-324", "79228162514264337593543950337",
            "1." + new string('3', 98) + "7", "-1." + new string('3', 98) + "7", new string('9', 1000), "-" + new string('9', 4001),
            "1." + new string('3', 7985) + "7", "1e999999999999999999999", "0." + new string('0', 999) + "1",
        ];
        var json = "[" + string.Join(",", numbers) + "]";

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"""
            SELECT CAST(j.[key] AS int), k.k, {SqlDigest.KeySql("k.k COLLATE Latin1_General_100_BIN2")}
            FROM OPENJSON(@json) AS j
            OUTER APPLY OPENJSON(JSON_ARRAY(dbo.JsonNumberOrderKey(j.[value]))) WITH (k varchar(max) '$') AS k
            """,
            connection);
        command.Parameters.Add("@json", System.Data.SqlDbType.NVarChar, -1).Value = json;

        var checkedCount = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var key = ExactDecimal.Parse(numbers[reader.GetInt32(0)]).ToOrderKey();
            reader.GetString(1).ShouldBe(key);
            reader.GetString(2).ShouldBe(SqlDigest.OfKey(key), string.Create(CultureInfo.InvariantCulture, $"number {reader.GetInt32(0)}"));
            reader.GetString(2).ShouldStartWith(key.Length.ToString(CultureInfo.InvariantCulture) + ":");
            checkedCount++;
        }

        checkedCount.ShouldBe(numbers.Length);
    }

    [Fact]
    public void Utf16Hex_IsTheLittleEndianCodeUnits()
    {
        SqlDigest.Utf16Hex("").ShouldBe("");
        SqlDigest.Utf16Hex("A|").ShouldBe("41007C00");
        SqlDigest.Utf16Hex("😀").ShouldBe("3DD800DE");
        SqlDigest.OfString("").ShouldBe("0:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([])));
        SqlDigest.OfString("😀").ShouldBe("2:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.Unicode.GetBytes("😀"))));
        SqlDigest.OfKey("21").ShouldBe("2:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes("21"))));
    }
}
