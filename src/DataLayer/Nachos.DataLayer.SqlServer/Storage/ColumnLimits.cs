using Nachos.Abstractions;

namespace Nachos.DataLayer.SqlServer.Storage;

/// <summary>
/// The lengths of the provider's bounded columns (in UTF-16 code units, as <c>nvarchar(n)</c> and <c>char(n)</c> count
/// them), checked before a write. An over-long value is then a <see cref="NachosValidationException"/> (422) with a fixed
/// message, instead of SQL Server's truncation error 2628, which is a 500 and echoes the start of the value. The in-memory
/// provider has no such limits; through the API, Core validates names and keys first, so only a grant's object id can
/// reach these checks.
/// </summary>
internal static class ColumnLimits
{
    /// <summary>Workspace, peer and session names.</summary>
    public const int Name = 512;

    /// <summary>A grant's principal object id.</summary>
    public const int ObjectId = 64;

    /// <summary>A grant's role.</summary>
    public const int Role = 32;

    /// <summary>An idempotency key.</summary>
    public const int IdempotencyKey = 255;

    /// <summary>An idempotency record's request hash.</summary>
    public const int RequestHash = 64;

    public static void RequireName(string name) =>
        Require(name, Name, "A workspace, peer or session name may hold at most 512 UTF-16 code units in the SQL Server provider.");

    public static void RequireNames(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            RequireName(name);
        }
    }

    public static void RequireGrant(string objectId, string role)
    {
        Require(objectId, ObjectId, "A grant's object id may hold at most 64 UTF-16 code units in the SQL Server provider.");
        Require(role, Role, "A grant's role may hold at most 32 UTF-16 code units in the SQL Server provider.");
    }

    public static void RequireIdempotency(string key, string requestHash)
    {
        Require(key, IdempotencyKey, "An idempotency key may hold at most 255 UTF-16 code units in the SQL Server provider.");
        Require(requestHash, RequestHash, "An idempotency request hash may hold at most 64 characters in the SQL Server provider.");
    }

    private static void Require(string value, int limit, string message)
    {
        if (value.Length > limit)
        {
            throw new NachosValidationException(message);
        }
    }
}
