using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions.Work;

/// <summary>A read <see cref="SummaryPayload"/>.</summary>
public sealed record SummaryWork(SummaryKind Kind, long ThroughSeq);
