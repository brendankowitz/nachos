using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions.Work;

/// <summary>
/// The payload of a <see cref="WorkTaskType.Representation"/> item:
/// <c>{"observers":[...], "reasoning_enabled"?: bool, "custom_instructions"?: string}</c>. The observers are those
/// resolved by <see cref="Observation.ObserverRule"/> when the message was appended, and the optional fields are the
/// message's own reasoning settings (see <see cref="DeriveIntent"/>). The snapshot is authoritative.
/// </summary>
public static class RepresentationPayload
{
    private const string ObserversKey = "observers";
    private const string ReasoningEnabledKey = "reasoning_enabled";
    private const string CustomInstructionsKey = "custom_instructions";

    /// <param name="observers">The observers resolved when the message was appended.</param>
    /// <param name="reasoningEnabled">The message's own <c>reasoning.enabled</c>; null leaves the field out.</param>
    /// <param name="customInstructions">The message's own <c>reasoning.custom_instructions</c>; null leaves the field out.</param>
    public static JsonObject Create(
        IReadOnlyList<string> observers, bool? reasoningEnabled = null, string? customInstructions = null)
    {
        ArgumentNullException.ThrowIfNull(observers);
        var payload = new JsonObject { [ObserversKey] = new JsonArray([.. observers.Select(observer => JsonValue.Create(observer))]) };
        if (reasoningEnabled is { } enabled)
        {
            payload[ReasoningEnabledKey] = enabled;
        }

        if (customInstructions is not null)
        {
            payload[CustomInstructionsKey] = customInstructions;
        }

        return payload;
    }

    /// <summary>Reads a payload; an absent (or JSON <c>null</c>) optional field reads as null.</summary>
    /// <exception cref="FormatException">
    /// The payload has no <c>observers</c> array of strings, or an optional field has the wrong type.
    /// </exception>
    public static RepresentationWork Read(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!payload.TryGetPropertyValue(ObserversKey, out var node) || node is not JsonArray array)
        {
            throw new FormatException("A representation work item's payload must have an 'observers' array.");
        }

        IReadOnlyList<string> observers = [.. array.Select(element => element is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : throw new FormatException("A representation work item's 'observers' must all be strings."))];

        return new RepresentationWork(
            observers,
            payload[ReasoningEnabledKey] switch
            {
                null => null,
                JsonValue value when value.GetValueKind() is JsonValueKind.True or JsonValueKind.False => value.GetValue<bool>(),
                _ => throw new FormatException($"A representation work item's '{ReasoningEnabledKey}' must be a boolean."),
            },
            payload[CustomInstructionsKey] switch
            {
                null => null,
                JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
                _ => throw new FormatException($"A representation work item's '{CustomInstructionsKey}' must be a string."),
            });
    }
}
