using System.Text.Json.Nodes;
using Nachos.Abstractions.Domain;

namespace Nachos.DataLayer.InMemory.Storage;

/// <summary>
/// The JSON isolation rule: the store owns private deep clones of what it is given and hands out fresh deep clones,
/// so no <see cref="JsonObject"/> is ever shared with a caller.
/// </summary>
internal static class JsonCopy
{
    /// <summary>A store-owned copy of an input value; null becomes <c>{}</c>.</summary>
    public static JsonObject Own(JsonObject? value) => value is null ? new JsonObject() : Clone(value);

    /// <summary>A store-owned copy of an optional replacement; null stays null (meaning "unchanged").</summary>
    public static JsonObject? OwnOptional(JsonObject? value) => value is null ? null : Clone(value);

    public static WorkspaceRecord Out(WorkspaceRecord record) =>
        record with { Metadata = Clone(record.Metadata), Configuration = Clone(record.Configuration) };

    public static PeerRecord Out(PeerRecord record) =>
        record with { Metadata = Clone(record.Metadata), Configuration = Clone(record.Configuration) };

    public static SessionRecord Out(SessionRecord record) =>
        record with { Metadata = Clone(record.Metadata), Configuration = Clone(record.Configuration) };

    public static MessageRecord Out(MessageRecord record) => record with { Metadata = Clone(record.Metadata) };

    private static JsonObject Clone(JsonObject value) => (JsonObject)value.DeepClone();
}
