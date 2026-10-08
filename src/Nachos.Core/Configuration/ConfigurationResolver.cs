using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Json;
using Nachos.Core.Tokens;
using Nachos.Core.Validation;

namespace Nachos.Core.Configuration;

public sealed class ConfigurationResolver : IConfigurationResolver
{
    private readonly WorkspaceConfiguration _global;
    private readonly RequestValidator _validator;
    private readonly int _maxTokensShort;
    private readonly int _maxTokensLong;
    private readonly int _maxCustomInstructionsTokens;

    public ConfigurationResolver(IOptions<NachosOptions> options, ITokenCounter tokenCounter)
    {
        _validator = new RequestValidator(options, tokenCounter);
        _global = options.Value.ToResourceConfiguration();
        var instructionErrors = _validator.GetInstructionBudgetErrors(_global).ToArray();
        if (instructionErrors.Length > 0)
        {
            throw new OptionsValidationException(Microsoft.Extensions.Options.Options.DefaultName,
                typeof(NachosOptions), instructionErrors);
        }

        _maxTokensShort = options.Value.Summary.MaxTokensShort;
        _maxTokensLong = options.Value.Summary.MaxTokensLong;
        _maxCustomInstructionsTokens = options.Value.Deriver.MaxCustomInstructionsTokens;
    }

    public ResolvedConfiguration Resolve(JsonObject? workspace, JsonObject? session = null, JsonObject? message = null)
    {
        var w = Read<WorkspaceConfiguration>(workspace);
        var s = Read<SessionConfiguration>(session);
        var m = Read<MessageConfiguration>(message);
        _validator.ValidateWorkspaceConfiguration(w);
        _validator.ValidateSessionConfiguration(s);
        _validator.ValidateMessageConfiguration(m);

        return new(
            new(
                Pick(_global.Reasoning?.Enabled, w?.Reasoning?.Enabled, s?.Reasoning?.Enabled, m?.Reasoning?.Enabled),
                Pick(_global.Reasoning?.CustomInstructions, w?.Reasoning?.CustomInstructions,
                    s?.Reasoning?.CustomInstructions, m?.Reasoning?.CustomInstructions)),
            new(
                Pick(_global.PeerCard?.Use, w?.PeerCard?.Use, s?.PeerCard?.Use),
                Pick(_global.PeerCard?.Create, w?.PeerCard?.Create, s?.PeerCard?.Create),
                Pick(_global.PeerCard?.CustomInstructions, w?.PeerCard?.CustomInstructions, s?.PeerCard?.CustomInstructions)),
            new(
                Pick(_global.Summary?.Enabled, w?.Summary?.Enabled, s?.Summary?.Enabled),
                PickCount(_global.Summary!.MessagesPerShortSummary!.Value,
                    w?.Summary?.MessagesPerShortSummary, s?.Summary?.MessagesPerShortSummary),
                PickCount(_global.Summary.MessagesPerLongSummary!.Value,
                    w?.Summary?.MessagesPerLongSummary, s?.Summary?.MessagesPerLongSummary),
                Global(_maxTokensShort),
                Global(_maxTokensLong),
                Pick(_global.Summary.CustomInstructions, w?.Summary?.CustomInstructions, s?.Summary?.CustomInstructions)),
            new(
                Pick(_global.Dream?.Enabled, w?.Dream?.Enabled, s?.Dream?.Enabled),
                Pick(_global.Dream?.CustomInstructions, w?.Dream?.CustomInstructions, s?.Dream?.CustomInstructions)),
            new(Pick(_global.Dialectic?.CustomInstructions, w?.Dialectic?.CustomInstructions, s?.Dialectic?.CustomInstructions)),
            Pick(_global.CustomInstructions, w?.CustomInstructions, s?.CustomInstructions),
            Global(_maxCustomInstructionsTokens));
    }

    private static T? Read<T>(JsonObject? configuration)
    {
        try
        {
            return StrictJsonData.ToCanonical(configuration) is { } canonical ? canonical.Deserialize<T>() : default;
        }
        catch (JsonException error)
        {
            var path = error.Path?.Split('.').Skip(1).Cast<object>() ?? [];
            throw new RequestValidationException(
                [new ValidationError(["body", "configuration", .. path],
                    "Configuration contains a value of the wrong type.", "value_error")], error);
        }
    }

    private static ResolvedValue<T> Pick<T>(T global, T? workspace, T? session, T? message = default) =>
        message is not null ? new(message, "message") :
        session is not null ? new(session, "session") :
        workspace is not null ? new(workspace, "workspace") :
        Global(global);

    private static ResolvedValue<int> PickCount(int global, int? workspace, int? session) =>
        session is not null ? new(session.Value, "session") :
        workspace is not null ? new(workspace.Value, "workspace") :
        Global(global);

    private static ResolvedValue<T> Global<T>(T value) => new(value, "global");
}
