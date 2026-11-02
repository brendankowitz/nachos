namespace Nachos.DataLayer.SqlServer.Entities;

/// <summary>
/// A <c>dbo.Conclusions</c> row with its collection's peer names and its session's name, as the conclusion reads select it
/// (an unmapped query type). <see cref="Distance"/> is the cosine distance to the query vector of a vector read, else null.
/// </summary>
internal sealed record ConclusionRow(
    long Id,
    string PublicId,
    string ObserverName,
    string ObservedName,
    string? SessionName,
    byte Level,
    string Content,
    int TimesDerived,
    string InternalMetadata,
    DateTimeOffset CreatedAt,
    double? Distance);
