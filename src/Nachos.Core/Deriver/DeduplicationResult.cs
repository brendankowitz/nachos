using Nachos.Abstractions.Domain;

namespace Nachos.Core.Deriver;

/// <summary>
/// What one batch of facts does to one collection. A conclusion id appears at most once across
/// <see cref="Corroborations"/> and <see cref="Replacements"/>, so one batch counts as one re-derivation of it.
/// </summary>
public sealed record DeduplicationResult(
    IReadOnlyList<DerivedFact> Inserts,
    IReadOnlyList<string> Corroborations,
    IReadOnlyList<ConclusionReplacement> Replacements);
