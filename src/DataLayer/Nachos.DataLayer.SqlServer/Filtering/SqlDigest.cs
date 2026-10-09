using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Nachos.DataLayer.SqlServer.Filtering;

/// <summary>
/// Packed entries for long <c>in</c> operands that SQL Server and .NET compute byte-identically: the operand's length,
/// <c>:</c>, and the upper-case hex SHA-256 of its bytes (<c>len:HEX</c>, at most 84 characters whatever the operand's
/// length). A filter packs its operands' entries and finds a stored value's entry among them with <c>CHARINDEX</c>.
/// Long-operand <c>in</c> membership is decided by SHA-256 digest + kind + length: the caller tests entries only against
/// stored values of the operand's JSON kind, so a false match needs a same-length SHA-256 collision (accepted as exact,
/// Task 7 review round 3).
/// </summary>
internal static class SqlDigest
{
    /// <summary>
    /// The entry of an <c>nvarchar</c> <paramref name="expression"/>: its UTF-16 code-unit count and the SHA-256 of its
    /// UTF-16LE code units. <c>HASHBYTES</c> has no input length limit on SQL Server 2016 and later.
    /// </summary>
    public static string StringSql(string expression) =>
        $"CONCAT(CONVERT(varchar(20), DATALENGTH({expression}) / 2), ':', CONVERT(varchar(64), HASHBYTES('SHA2_256', {expression}), 2))";

    /// <summary>The entry of an order-key <paramref name="expression"/> (ASCII <c>varchar</c>): its length and the SHA-256 of its bytes.</summary>
    public static string KeySql(string expression) =>
        $"CONCAT(CONVERT(varchar(20), DATALENGTH({expression})), ':', CONVERT(varchar(64), HASHBYTES('SHA2_256', {expression}), 2))";

    /// <summary>The entry of a string, as <see cref="StringSql"/> computes it for an <c>nvarchar</c> holding it.</summary>
    public static string OfString(string value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value.Length}:{Convert.ToHexString(SHA256.HashData(Utf16LittleEndian(value)))}");

    /// <summary>The entry of an order key (ASCII digits and <c>:</c>), as <see cref="KeySql"/> computes it.</summary>
    public static string OfKey(string key) =>
        string.Create(CultureInfo.InvariantCulture, $"{key.Length}:{Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(key)))}");

    /// <summary>
    /// The upper-case hex of a string's UTF-16LE code units, as <c>CONVERT(varchar, CAST(nvarchar AS varbinary), 2)</c>
    /// writes it: the exact entry of a short string.
    /// </summary>
    public static string Utf16Hex(string value) => Convert.ToHexString(Utf16LittleEndian(value));

    /// <summary>
    /// The string's code units as UTF-16LE bytes, taken as they are rather than through an encoder. Unpaired surrogates
    /// cannot reach a filter (the parser and the store reject them), so this only keeps the bytes exact defensively.
    /// </summary>
    private static ReadOnlySpan<byte> Utf16LittleEndian(string value)
    {
        if (!BitConverter.IsLittleEndian)
        {
            return Encoding.Unicode.GetBytes(value);
        }

        return MemoryMarshal.AsBytes(value.AsSpan());
    }
}
