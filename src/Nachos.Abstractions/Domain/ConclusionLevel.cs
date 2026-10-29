namespace Nachos.Abstractions.Domain;

/// <summary>The reasoning level of a conclusion; on the wire the lowercase member name.</summary>
public enum ConclusionLevel
{
    /// <summary>A fact the observed peer stated, attributable to the session it was said in.</summary>
    Explicit,

    /// <summary>A conclusion deduced from other conclusions (dreams, M5).</summary>
    Deductive,

    /// <summary>A pattern induced from other conclusions (dreams, M5).</summary>
    Inductive,

    /// <summary>A recorded contradiction between conclusions (dreams, M5).</summary>
    Contradiction,
}
