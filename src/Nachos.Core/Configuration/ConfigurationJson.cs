using System.Text.Json.Nodes;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Json;

namespace Nachos.Core.Configuration;

/// <summary>Projects sealed configuration contracts as literal data before any serializer can rewrite their strings.</summary>
internal static class ConfigurationJson
{
    public static JsonObject? Project(WorkspaceConfiguration? value) =>
        value is null ? null : StrictJsonData.ToCanonical(new JsonObject
        {
            ["reasoning"] = Reasoning(value.Reasoning),
            ["peer_card"] = value.PeerCard is { } card ? new JsonObject
            {
                ["use"] = card.Use, ["create"] = card.Create, ["custom_instructions"] = card.CustomInstructions,
            } : null,
            ["summary"] = value.Summary is { } summary ? new JsonObject
            {
                ["enabled"] = summary.Enabled,
                ["messages_per_short_summary"] = summary.MessagesPerShortSummary,
                ["messages_per_long_summary"] = summary.MessagesPerLongSummary,
                ["custom_instructions"] = summary.CustomInstructions,
            } : null,
            ["dream"] = value.Dream is { } dream ? new JsonObject
            {
                ["enabled"] = dream.Enabled, ["custom_instructions"] = dream.CustomInstructions,
            } : null,
            ["dialectic"] = value.Dialectic is { } dialectic ? new JsonObject
            {
                ["custom_instructions"] = dialectic.CustomInstructions,
            } : null,
            ["custom_instructions"] = value.CustomInstructions,
        })!.AsObject();

    public static JsonObject? Project(SessionConfiguration? value) => Project(value is null ? null :
        new WorkspaceConfiguration(value.Reasoning, value.PeerCard, value.Summary, value.Dream,
            value.Dialectic, value.CustomInstructions));

    public static JsonObject? Project(MessageConfiguration? value) =>
        value is null ? null : StrictJsonData.ToCanonical(new JsonObject { ["reasoning"] = Reasoning(value.Reasoning) })!.AsObject();

    private static JsonObject? Reasoning(ReasoningConfiguration? value) => value is null ? null : new()
    {
        ["enabled"] = value.Enabled, ["custom_instructions"] = value.CustomInstructions,
    };
}
