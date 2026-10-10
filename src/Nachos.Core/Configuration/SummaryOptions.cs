namespace Nachos.Core.Configuration;

/// <summary>Summary cadence and output budgets. Token budgets are deployment-only, not wire fields.</summary>
public sealed class SummaryOptions
{
    public bool? Enabled { get; set; }

    public int MessagesPerShort { get; set; } = 20;

    public int MessagesPerLong { get; set; } = 60;

    public int MaxTokensShort { get; set; } = 1000;

    public int MaxTokensLong { get; set; } = 4000;

    public string? CustomInstructions { get; set; }
}
