using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions.Work;

/// <summary>
/// The payload of a <see cref="WorkTaskType.Representation"/> item: <c>{"observers":[...]}</c>, the observers resolved
/// by <see cref="Observation.ObserverRule"/> when the message was appended. The snapshot is authoritative.
/// </summary>
public static class RepresentationPayload
{
    private const string ObserversKey = "observers";

    public static JsonObject Create(IReadOnlyList<string> observers)
    {
        ArgumentNullException.ThrowIfNull(observers);
        return new JsonObject { [ObserversKey] = new JsonArray([.. observers.Select(observer => JsonValue.Create(observer))]) };
    }

    /// <exception cref="FormatException">The payload has no <c>observers</c> array of strings.</exception>
    public static IReadOnlyList<string> ReadObservers(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!payload.TryGetPropertyValue(ObserversKey, out var node) || node is not JsonArray array)
        {
            throw new FormatException("A representation work item's payload must have an 'observers' array.");
        }

        return [.. array.Select(element => element is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : throw new FormatException("A representation work item's 'observers' must all be strings."))];
    }
}
