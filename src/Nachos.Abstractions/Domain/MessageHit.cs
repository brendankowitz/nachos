namespace Nachos.Abstractions.Domain;

/// <summary>One search result.</summary>
/// <param name="Rank">The 1-based position in its result list: the input of rank fusion.</param>
public sealed record MessageHit(MessageRecord Message, int Rank);
