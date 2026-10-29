namespace Nachos.Abstractions.Domain;

/// <summary>
/// Every write of one derivation, applied atomically by <see cref="Stores.IConclusionStore.CommitDerivationAsync"/>.
/// The collections' dream counters are derived from these writes by the store.
/// </summary>
/// <param name="Inserts">New conclusions, in any collection of the workspace.</param>
/// <param name="Corroborations">
/// Public ids of conclusions that were derived again; each occurrence adds one to <c>TimesDerived</c>.
/// </param>
/// <param name="Replacements">Content replacements.</param>
public sealed record DerivationChanges(
    string WorkspaceName,
    IReadOnlyList<NewConclusion> Inserts,
    IReadOnlyList<string> Corroborations,
    IReadOnlyList<ConclusionReplacement> Replacements);
