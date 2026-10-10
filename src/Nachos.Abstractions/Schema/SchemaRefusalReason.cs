namespace Nachos.Abstractions.Schema;

/// <summary>Why <see cref="ISchemaManager.DeployAsync"/> declined to change a database.</summary>
public enum SchemaRefusalReason
{
    /// <summary>The database schema is newer than this build expects. Nachos never downgrades.</summary>
    Ahead,

    /// <summary>The database has objects but no Nachos schema version, and adopting it was not requested.</summary>
    Unstamped,

    /// <summary>The change would drop data and data loss was not allowed.</summary>
    DataLossBlocked,

    /// <summary>The change is not <see cref="DeployClassification.AutoSafe"/> and nobody has reviewed it.</summary>
    NotAutoSafe,
}
