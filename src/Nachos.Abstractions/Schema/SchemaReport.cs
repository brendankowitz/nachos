namespace Nachos.Abstractions.Schema;

/// <summary>The outcome of comparing the embedded schema with a database.</summary>
/// <param name="Classification">Whether the pending changes may be applied automatically.</param>
/// <param name="ReportXml">The raw DacFx deploy report.</param>
public sealed record SchemaReport(DeployClassification Classification, string ReportXml);