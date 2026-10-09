using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Nachos.DataLayer.SqlServer.Filtering;

/// <summary>
/// Prefilter entries for long <c>in</c> operands that SQL Server and .NET compute byte-identically: the operand's
/// length, <c>:</c>, and the upper-case hex SHA-256 of its bytes (<c>len:HEX</c>, at most 84 characters whatever the
/// operand's length). Long-operand <c>in</c> membership uses a SHA-256 + kind + length prefilter, confirmed by exact
/// comparison: an entry match only selects the one operand to compare the stored value with byte for byte
/// (see <c>FilterWriter.VerifiedIn</c>), so a digest collision can cost time but never change a result.
/// </summary>
internal static class SqlDigest
{
    /// <summary>The hex digits of a full SHA-256. Shorter lengths exist only to force collisions in tests.</summary>
    public const int FullHexLength = 64;

    /// <summary>
    /// The entry of an <c>nvarchar</c> <paramref name="expression"/>: its UTF-16 code-unit count and the SHA-256 of its
    /// UTF-16LE code units. <c>HASHBYTES</c> has no input length limit on SQL Server 2016 and later.
    /// </summary>
    public static string StringSql(string expression, int hexLength = FullHexLength) =>
        $"CONCAT(CONVERT(varchar(20), DATALENGTH({expression}) / 2), ':', {HashSql(expression, hexLength)})";

    /// <summary>The entry of an order-key <paramref name="expression"/> (ASCII <c>varchar</c>): its length and the SHA-256 of its bytes.</summary>
    public static string KeySql(string expression, int hexLength = FullHexLength) =>
        $"CONCAT(CONVERT(varchar(20), DATALENGTH({expression})), ':', {HashSql(expression, hexLength)})";

    /// <summary>The entry of a string, as <see cref="StringSql"/> computes it for an <c>nvarchar</c> holding it.</summary>
    public static string OfString(string value, int hexLength = FullHexLength) =>
        Entry(value.Length, SHA256.HashData(Utf16Bytes(value)), hexLength);

    /// <summary>The entry of an order key (ASCII digits and <c>:</c>), as <see cref="KeySql"/> computes it.</summary>
    public static string OfKey(string key, int hexLength = FullHexLength) =>
        Entry(key.Length, SHA256.HashData(KeyBytes(key)), hexLength);

    /// <summary>
    /// The upper-case hex of a string's UTF-16LE code units, as <c>CONVERT(varchar, CAST(nvarchar AS varbinary), 2)</c>
    /// writes it: the exact entry of a short string.
    /// </summary>
    public static string Utf16Hex(string value) => Convert.ToHexString(Utf16Bytes(value));

    /// <summary>
    /// The string's code units as UTF-16LE bytes (what <c>CAST(nvarchar AS varbinary)</c> yields), taken as they are
    /// rather than through an encoder. Unpaired surrogates cannot reach a filter (the parser and the store reject them),
    /// so this only keeps the bytes exact defensively.
    /// </summary>
    public static byte[] Utf16Bytes(string value) =>
        BitConverter.IsLittleEndian ? MemoryMarshal.AsBytes(value.AsSpan()).ToArray() : Encoding.Unicode.GetBytes(value);

    /// <summary>An order key's bytes, as <c>CAST(varchar AS varbinary)</c> yields them (keys are ASCII).</summary>
    public static byte[] KeyBytes(string key) => Encoding.ASCII.GetBytes(key);

    private static string HashSql(string expression, int hexLength)
    {
        ValidateHexLength(hexLength);
        var hash = $"CONVERT(varchar(64), HASHBYTES('SHA2_256', {expression}), 2)";
        return hexLength == FullHexLength ? hash : $"LEFT({hash}, {hexLength.ToString(CultureInfo.InvariantCulture)})";
    }

    private static string Entry(int length, byte[] hash, int hexLength)
    {
        ValidateHexLength(hexLength);
        return string.Create(CultureInfo.InvariantCulture, $"{length}:{Convert.ToHexString(hash)[..hexLength]}");
    }

    private static void ValidateHexLength(int hexLength) => ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)hexLength, (uint)FullHexLength, nameof(hexLength));
}