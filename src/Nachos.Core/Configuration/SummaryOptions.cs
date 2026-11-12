namespace Nachos.Core.Configuration;

/// <summary>Summary cadence and output budgets. Token budgets are deployment-only, not wire fields.</summary>
public sealed class SummaryOptions
{
    /// <summary>
    /// The deployment default of <c>summary.enabled</c>; a workspace or session setting overrides it. Null, the default,
    /// leaves summaries on.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>A short summary is made every this many messages of a session, at least 10; default 20.</summary>
    public int MessagesPerShort { get; set; } = 20;

    /// <summary>A long summary is made every this many messages of a session, at least 20; default 60.</summary>
    public int MessagesPerLong { get; set; } = 60;

    /// <summary>The most tokens the model may write for a short summary; default 1,000.</summary>
    public int MaxTokensShort { get; set; } = 1000;

    /// <summary>The most tokens the model may write for a long summary; default 4,000.</summary>
    public int MaxTokensLong { get; set; } = 4000;

    /// <summary>
    /// Token budget of one summary request: prompt, prior summary and messages; default 25,000. When the new messages
    /// exceed it the oldest are left out of the request.
    /// </summary>
    public int MaxInputTokens { get; set; } = 25_000;

    /// <summary>The deployment default of <c>summary.custom_instructions</c>, added to the summarizer's system prompt.</summary>
    public string? CustomInstructions { get; set; }
}
