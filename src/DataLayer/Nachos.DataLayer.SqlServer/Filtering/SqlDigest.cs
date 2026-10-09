using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Nachos.DataLayer.SqlServer.Filtering;

/// <summary>
/// SHA-256 digests that SQL Server and .NET compute byte-identically, so a filter can pack fixed-width digests of its
/// operands (66 characters per entry, whatever the operand's length) and find a stored value's digest among them with
/// <c>CHARINDEX</c>. Digest equality is accepted as value equality (SHA-256 collision resistance; review round 3).
/// </summary>
internal static class SqlDigest
{
    /// <summary>
    /// The upper-case hex SHA-256 of the bytes of <paramref name="expression"/>: UTF-16LE code units for <c>nvarchar</c>,
    /// single bytes for an ASCII <c>varchar</c>. <c>HASHBYTES</c> has no input length limit on SQL Server 2016 and later.
    /// </summary>
    public static string Sql(string expression) => $"CONVERT(varchar(64), HASHBYTES('SHA2_256', {expression}), 2)";

    /// <summary>The digest of a string as SQL Server hashes an <c>nvarchar</c> holding it: its UTF-16LE code units.</summary>
    public static string OfString(string value) => Convert.ToHexString(SHA256.HashData(Utf16LittleEndian(value)));

    /// <summary>The digest of an order key (ASCII digits and <c>:</c>) as SQL Server hashes it as <c>varchar</c>.</summary>
    public static string OfKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(key)));

    /// <summary>
    /// The upper-case hex of a string's UTF-16LE code units, as <c>CONVERT(varchar, CAST(nvarchar AS varbinary), 2)</c>
    /// writes it.
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
