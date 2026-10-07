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
    /// Applies the embedded schema. Unless <paramref name="allowDataLoss"/> is set, changes that are not
    /// <see cref="DeployClassification.AutoSafe"/> are not applied: the report is returned so the caller can
    /// inspect it. With <paramref name="allowDataLoss"/> the operator accepts whatever the report contains.
    /// </summary>
    /// <returns>The report the deploy was based on; check <see cref="SchemaReport.Classification"/>.</returns>
    Task<SchemaReport> DeployAsync(bool allowDataLoss, CancellationToken ct);
}