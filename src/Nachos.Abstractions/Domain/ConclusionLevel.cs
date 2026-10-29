namespace Nachos.Abstractions.Domain;

/// <summary>The reasoning level of a conclusion; on the wire the lowercase member name. The values are the stored form.</summary>
public enum ConclusionLevel
{
    /// <summary>A fact the observed peer stated, attributable to the session it was said in.</summary>
    Explicit = 0,

    /// <summary>A conclusion deduced from other conclusions (dreams, M5).</summary>
    Deductive = 1,

    /// <summary>A pattern induced from other conclusions (dreams, M5).</summary>
    Inductive = 2,

    /// <summary>A recorded contradiction between conclusions (dreams, M5).</summary>
    Contradiction = 3,
}
