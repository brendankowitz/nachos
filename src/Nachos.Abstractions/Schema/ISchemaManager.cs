namespace Nachos.Abstractions.Schema;

/// <summary>Inspects and upgrades the relational schema of the configured database.</summary>
public interface ISchemaManager
{
    /// <summary>Reads the database's schema position without changing it.</summary>
    /// <exception cref="NotSupportedException">The server platform has no matching schema package.</exception>
    Task<SchemaStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>Computes and classifies the changes a deploy would make, without applying them.</summary>
    Task<SchemaReport> ReportAsync(CancellationToken ct);

    /// <summary>
    /// Applies the embedded schema, holding a cross-process lock for the whole operation. A change is applied only when
    /// the database is <see cref="SchemaState.Empty"/> (bootstrap), the classification is
    /// <see cref="DeployClassification.AutoSafe"/>, or <paramref name="approval"/> is
    /// <see cref="DeployApproval.OperatorReviewed"/>. Otherwise nothing is changed and the report comes back with
    /// <see cref="SchemaReport.Applied"/> false. After applying, the database must be current with an empty report,
    /// or this throws.
    /// </summary>
    /// <param name="approval">Who vouches for the change.</param>
    /// <param name="allowDataLoss">Lets the deploy drop data. Requires <see cref="DeployApproval.OperatorReviewed"/>; DacFx blocks data loss otherwise.</param>
    /// <param name="adoptUnstamped">Allows deploying into a database that has objects but no schema version. The normal classification still applies.</param>
    /// <exception cref="InvalidOperationException">The database is <see cref="SchemaState.Ahead"/> (never downgraded), is <see cref="SchemaState.Unstamped"/> without <paramref name="adoptUnstamped"/>, or does not match the schema after the deploy.</exception>
    /// <exception cref="ArgumentException"><paramref name="allowDataLoss"/> without <see cref="DeployApproval.OperatorReviewed"/>.</exception>
    /// <exception cref="TimeoutException">Another process held the schema lock for too long.</exception>
    Task<SchemaReport> DeployAsync(DeployApproval approval, bool allowDataLoss, bool adoptUnstamped, CancellationToken ct);
}