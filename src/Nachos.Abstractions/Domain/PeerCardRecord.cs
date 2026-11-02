namespace Nachos.Abstractions.Domain;

/// <summary>The card <paramref name="ObserverName"/> keeps about <paramref name="ObservedName"/>: short entries, in order.</summary>
public sealed record PeerCardRecord(
    string WorkspaceName,
    string ObserverName,
    string ObservedName,
    IReadOnlyList<string> Entries,
    DateTimeOffset UpdatedAt,
    PeerCardAuthor UpdatedBy);
