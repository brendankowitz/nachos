using Microsoft.Extensions.Options;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Core.Configuration;
using Nachos.Core.Tokens;

namespace Nachos.Core.Validation;

/// <summary>Validates caller-owned requests before any store mutation.</summary>
public sealed class RequestValidator
{
    private readonly int _maxCustomInstructionsTokens;
    private readonly ITokenCounter _tokenCounter;

    public RequestValidator(IOptions<NachosOptions> options, ITokenCounter tokenCounter)
    {
        options.Value.Validate();
        _maxCustomInstructionsTokens = options.Value.Deriver.MaxCustomInstructionsTokens;
        _tokenCounter = tokenCounter;
    }

    public void ValidateMessages(IReadOnlyList<MessageCreate> messages)
    {
        if (messages is null)
        {
            throw Invalid(["body", "messages"], "Messages must be an array.", "list_type");
        }

        if (messages.Count is < 1 or > 100)
        {
            throw Invalid(["body", "messages"], "A batch must contain 1–100 messages.",
                messages.Count < 1 ? "too_short" : "too_long");
        }

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message is null)
            {
                throw Invalid(["body", "messages", index], "A message must be an object.", "model_type");
            }

            if (message.Content is null)
            {
                throw Invalid(["body", "messages", index, "content"], "Content must be a string.", "string_type");
            }

            var characters = 0;
            foreach (var rune in message.Content.EnumerateRunes())
            {
                // OpenAPI string lengths count Unicode characters, not UTF-16 code units.
                if (++characters > 25000)
                {
                    throw Invalid(["body", "messages", index, "content"],
                        "Content must have at most 25000 characters.", "string_too_long");
                }
            }

            IdValidator.Validate(message.PeerId, "peer_id");
            ValidateMessageConfiguration(message.Configuration);
        }
    }

    public void ValidateWorkspaceConfiguration(WorkspaceConfiguration? configuration)
    {
        if (configuration is null)
        {
            return;
        }

        if (configuration.Summary?.MessagesPerShortSummary is < 10 ||
            configuration.Summary?.MessagesPerLongSummary is < 20)
        {
            throw new NachosValidationException("Summary cadence must be at least 10 short and 20 long messages.");
        }

        if (GetInstructionBudgetErrors(configuration).Any())
        {
            throw new NachosValidationException("Custom instructions exceed the configured token limit.");
        }
    }

    public void ValidateSessionConfiguration(SessionConfiguration? configuration) =>
        ValidateWorkspaceConfiguration(configuration is null ? null :
            new WorkspaceConfiguration(configuration.Reasoning, configuration.PeerCard, configuration.Summary,
                configuration.Dream, configuration.Dialectic, configuration.CustomInstructions));

    public void ValidateMessageConfiguration(MessageConfiguration? configuration) =>
        ValidateInstructions(configuration?.Reasoning?.CustomInstructions);

    internal IEnumerable<string> GetInstructionBudgetErrors(WorkspaceConfiguration configuration)
    {
        (string Property, string? Value)[] instructions =
        [
            (nameof(configuration.CustomInstructions), configuration.CustomInstructions),
            ($"{nameof(configuration.Reasoning)}.{nameof(ReasoningConfiguration.CustomInstructions)}",
                configuration.Reasoning?.CustomInstructions),
            ($"{nameof(configuration.PeerCard)}.{nameof(PeerCardConfiguration.CustomInstructions)}",
                configuration.PeerCard?.CustomInstructions),
            ($"{nameof(configuration.Summary)}.{nameof(SummaryConfiguration.CustomInstructions)}",
                configuration.Summary?.CustomInstructions),
            ($"{nameof(configuration.Dream)}.{nameof(DreamConfiguration.CustomInstructions)}",
                configuration.Dream?.CustomInstructions),
            ($"{nameof(configuration.Dialectic)}.{nameof(DialecticConfiguration.CustomInstructions)}",
                configuration.Dialectic?.CustomInstructions),
        ];

        foreach (var (property, value) in instructions)
        {
            if (ExceedsInstructionBudget(value))
            {
                yield return $"{property} exceeds the configured limit of {_maxCustomInstructionsTokens} tokens.";
            }
        }
    }

    private void ValidateInstructions(string? instructions)
    {
        if (ExceedsInstructionBudget(instructions))
        {
            throw new NachosValidationException("Custom instructions exceed the configured token limit.");
        }
    }

    private bool ExceedsInstructionBudget(string? instructions) =>
        instructions is not null && _tokenCounter.Count(instructions) > _maxCustomInstructionsTokens;

    private static RequestValidationException Invalid(IReadOnlyList<object> location, string message, string type) =>
        new([new ValidationError(location, message, type)]);
}
