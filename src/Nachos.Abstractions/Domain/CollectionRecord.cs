namespace Nachos.Abstractions.Domain;

/// <summary>
/// The collection of conclusions <paramref name="ObserverName"/> holds about <paramref name="ObservedName"/>, with the
/// counters that will schedule dreams (M5).
/// </summary>
/// <param name="NewExplicitCount">Explicit conclusions inserted by derivations since the last dream.</param>
/// <param name="LastActivityAt">When a derivation last changed the collection; null before the first.</param>
/// <param name="LastDreamAt">When a dream last ran on the collection; null until M5 runs one.</param>
public sealed record CollectionRecord(
    string WorkspaceName,
    string ObserverName,
    string ObservedName,
    int NewExplicitCount,
    DateTimeOffset? LastActivityAt,
    DateTimeOffset? LastDreamAt,
    DateTimeOffset CreatedAt);
