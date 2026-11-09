namespace Nachos.Core.Configuration;

/// <summary>Summary cadence and output budgets. Token budgets are deployment-only, not wire fields.</summary>
public sealed class SummaryOptions
{
    public bool? Enabled { get; set; }

    public int MessagesPerShort { get; set; } = 20;

    public int MessagesPerLong { get; set; } = 60;

    public int MaxTokensShort { get; set; } = 1000;

    public int MaxTokensLong { get; set; } = 4000;

    /// <summary>
    /// Token budget of one summary request: prompt, prior summary and messages; default 25,000. When the new messages
    /// exceed it the oldest are left out of the request.
    /// </summary>
    public int MaxInputTokens { get; set; } = 25_000;

    public string? CustomInstructions { get; set; }
}
