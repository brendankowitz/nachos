namespace Nachos.DataLayer.SqlServer.Entities;

/// <summary>
/// An open <c>dbo.WorkItems</c> row as a claim reads it, with the names its keys point at (an unmapped query type):
/// <see cref="MessageId"/> is the message's public id.
/// </summary>
internal sealed record WorkItemRow(
    long Id,
    string? WorkspaceName,
    byte TaskType,
    string Payload,
    int Attempts,
    DateTimeOffset AvailableAt,
    int TokenCount,
    string? SessionName,
    string? MessageId,
    DateTimeOffset CreatedAt);
