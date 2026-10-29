namespace Nachos.Core.Configuration;

/// <summary>Deriver budgets and queue policy. Defaults are the Honcho parity values (spec §8).</summary>
public sealed class DeriverOptions
{
    /// <summary>Maximum o200k_base tokens in each custom-instruction value; default 2000.</summary>
    public int MaxCustomInstructionsTokens { get; set; } = 2000;

    /// <summary>A unit is due once its pending messages reach this many tokens; default 512.</summary>
    public int EligibilityTokens { get; set; } = 512;

    /// <summary>One lease takes messages up to this many tokens (always at least one); default 1024.</summary>
    public int BatchTokenTarget { get; set; } = 1024;

    /// <summary>A unit is due once its oldest pending message is this old; default 30 minutes.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Token budget of one extraction request: prompt, instructions and transcript; default 25,000. The structured-output
    /// JSON schema the chat client adds to the request is not counted, so leave headroom below the model's window.
    /// </summary>
    public int MaxInputTokens { get; set; } = 25_000;

    /// <summary>A new fact within this cosine distance of an existing conclusion is a duplicate; default 0.05.</summary>
    public double DedupMaxDistance { get; set; } = 0.05;

    /// <summary>Attempts before a unit's items are dead-lettered; default 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>How long one lease is held before it must be renewed; default 5 minutes.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);
}
