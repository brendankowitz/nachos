namespace Nachos.Abstractions.Schema;

/// <summary>Where a database stands relative to the schema this build of Nachos expects.</summary>
public enum SchemaState
{
    /// <summary>The database has no user objects at all.</summary>
    Empty,

    /// <summary>The database has user objects but no stamped schema version, so it was not created by Nachos.</summary>
    Unstamped,

    /// <summary>The stamped version equals the version this build expects.</summary>
    Current,

    /// <summary>The stamped version is older than this build expects.</summary>
    Behind,

    /// <summary>The stamped version is newer than this build expects (a newer Nachos wrote it).</summary>
    Ahead,
}