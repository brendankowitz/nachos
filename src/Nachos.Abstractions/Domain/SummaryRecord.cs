namespace Nachos.Abstractions.Domain;

/// <summary>A session's current summary of one kind.</summary>
/// <param name="CoversThroughMessageId">The public id of the last message the summary covers.</param>
/// <param name="CoversThroughSeq">
/// That message's <c>Seq</c>. A save replaces the stored summary of the kind only when this is greater.
/// </param>
/// <param name="TokenCount">The token count of <paramref name="Content"/>.</param>
public sealed record SummaryRecord(
    SummaryKind Kind,
    string Content,
    string CoversThroughMessageId,
    long CoversThroughSeq,
    int TokenCount,
    DateTimeOffset CreatedAt);
