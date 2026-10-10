namespace Nachos.Abstractions.Schema;

/// <summary>The outcome of comparing the embedded schema with a database.</summary>
/// <param name="Classification">Whether the pending changes may be applied without review. Includes database-option differences that the raw report cannot show.</param>
/// <param name="ReportXml">The raw DacFx deploy report.</param>
/// <param name="Applied">
/// True only when a deploy was executed and verified. False means nothing was changed: for a deploy, the database
/// already matches (<see cref="HasPendingChanges"/> is false); a refused deploy throws <see cref="SchemaDeployRefusedException"/>
/// instead. A report that was only computed (<see cref="ISchemaManager.ReportAsync"/>) is never applied.
/// </param>
/// <param name="HasPendingChanges">True when the database differs from the embedded schema, as computed before any deploy.</param>
/// <param name="Reasons">Findings beyond the raw report that made the change unsafe, for example a database option that differs.</param>
public sealed record SchemaReport(
    DeployClassification Classification,
    string ReportXml,
    bool Applied,
    bool HasPendingChanges,
    IReadOnlyList<string> Reasons);
