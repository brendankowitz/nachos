namespace Nachos.Core.Configuration;

public sealed class DeriverOptions
{
    /// <summary>Maximum o200k_base tokens in each custom-instruction value; default 2000.</summary>
    public int MaxCustomInstructionsTokens { get; set; } = 2000;
}
