using System.Data;
using Microsoft.Data.SqlClient;

namespace Nachos.DataLayer.SqlServer.Storage;

/// <summary>
/// Typed parameter factories. Text parameters are never sized to a column: a value longer than its column must fail or
/// not match, never be silently truncated into a different value.
/// </summary>
internal static class SqlParameters
{
    /// <summary>The longest <c>nvarchar(n)</c>; longer text is sent as <c>nvarchar(max)</c>.</summary>
    private const int MaxSizedText = 4000;

    public static SqlParameter Text(string name, string value) =>
        new(name, SqlDbType.NVarChar, value.Length <= MaxSizedText ? MaxSizedText : -1) { Value = value };

    /// <summary>Unbounded text, such as JSON for a <c>json</c> column or an <c>OPENJSON</c> list.</summary>
    public static SqlParameter LongText(string name, string value) => new(name, SqlDbType.NVarChar, -1) { Value = value };

    public static SqlParameter Ascii(string name, string value) =>
        new(name, SqlDbType.VarChar, value.Length <= 8000 ? 8000 : -1) { Value = value };

    public static SqlParameter Time(string name, DateTimeOffset value) =>
        new(name, SqlDbType.DateTimeOffset) { Scale = 7, Value = value };

    public static SqlParameter Long(string name, long value) => new(name, SqlDbType.BigInt) { Value = value };

    public static SqlParameter Int(string name, int value) => new(name, SqlDbType.Int) { Value = value };

    public static SqlParameter Bit(string name, bool value) => new(name, SqlDbType.Bit) { Value = value };

    public static SqlParameter Decimal(string name, decimal value) =>
        new(name, SqlDbType.Decimal) { Precision = 38, Scale = 0, Value = value };

    public static SqlParameter Binary(string name, byte[] value) => new(name, SqlDbType.Binary, value.Length) { Value = value };
}
