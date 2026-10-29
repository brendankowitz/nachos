namespace Nachos.Abstractions.Domain;

/// <summary>The wire names of <see cref="ConclusionLevel"/>, also the text of the <see cref="Filtering.FilterColumns.Level"/> column.</summary>
public static class ConclusionLevelNames
{
    public const string Explicit = "explicit";
    public const string Deductive = "deductive";
    public const string Inductive = "inductive";
    public const string Contradiction = "contradiction";

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not a defined level.</exception>
    public static string Of(ConclusionLevel level) => level switch
    {
        ConclusionLevel.Explicit => Explicit,
        ConclusionLevel.Deductive => Deductive,
        ConclusionLevel.Inductive => Inductive,
        ConclusionLevel.Contradiction => Contradiction,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown conclusion level."),
    };
}
