using Microsoft.Extensions.Options;
using Nachos.Abstractions.Contracts;

namespace Nachos.Core.Configuration;

/// <summary>
/// Deployment defaults for M1 resource configuration. Unspecified nullable flags and strings stay null;
/// the public contract does not define an enabled/disabled default for them.
/// </summary>
public sealed class NachosOptions
{
    public ReasoningConfiguration Reasoning { get; set; } = new();

    public PeerCardConfiguration PeerCard { get; set; } = new();

    public SummaryOptions Summary { get; set; } = new();

    public DreamConfiguration Dream { get; set; } = new();

    public DialecticConfiguration Dialectic { get; set; } = new();

    public string? CustomInstructions { get; set; }

    public DeriverOptions Deriver { get; set; } = new();

    internal void Validate()
    {
        List<string> errors = [];
        if (Summary is null)
        {
            errors.Add("Summary is required.");
        }
        else
        {
            if (Summary.MessagesPerShort < 10)
            {
                errors.Add("Summary.MessagesPerShort must be at least 10.");
            }

            if (Summary.MessagesPerLong < 20)
            {
                errors.Add("Summary.MessagesPerLong must be at least 20.");
            }

            if (Summary.MaxTokensShort < 1 || Summary.MaxTokensLong < 1)
            {
                errors.Add("Summary token budgets must be positive.");
            }
        }

        if (Deriver is null)
        {
            errors.Add("Deriver is required.");
        }
        else if (Deriver.MaxCustomInstructionsTokens < 1)
        {
            errors.Add("Deriver.MaxCustomInstructionsTokens must be positive.");
        }

        if (errors.Count > 0)
        {
            throw new OptionsValidationException(Microsoft.Extensions.Options.Options.DefaultName,
                typeof(NachosOptions), errors);
        }
    }

    internal WorkspaceConfiguration ToResourceConfiguration() =>
        new(Reasoning, PeerCard,
            new SummaryConfiguration(Summary.Enabled, Summary.MessagesPerShort, Summary.MessagesPerLong,
                Summary.CustomInstructions),
            Dream, Dialectic, CustomInstructions);
}
