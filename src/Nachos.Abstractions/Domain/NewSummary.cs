namespace Nachos.Abstractions.Domain;

/// <summary>
/// A summary to save with <see cref="Stores.ISummaryStore.SaveAsync"/>; the store stamps its
/// <see cref="SummaryRecord.CreatedAt"/> from its clock.
/// </summary>
/// <param name="CoversThroughMessageId">The public id of the last message the summary covers.</param>
/// <param name="CoversThroughSeq">That message's <c>Seq</c>.</param>
/// <param name="TokenCount">The token count of <paramref name="Content"/>.</param>
public sealed record NewSummary(
    SummaryKind Kind, string Content, string CoversThroughMessageId, long CoversThroughSeq, int TokenCount);
