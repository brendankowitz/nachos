using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions.Work;

/// <summary>
/// The payload of a <see cref="WorkTaskType.Deletion"/> item: <c>{"kind": "workspace"}</c> or
/// <c>{"kind": "session", "session": "&lt;name&gt;"}</c>. The workspace is the item's own
/// <see cref="WorkItemRecord.WorkspaceName"/>. The session's name is in the payload because the item outlives the session
/// row, and a provider that resolves <see cref="WorkItemRecord.SessionName"/> through that row loses it on removal.
/// </summary>
public static class DeletionPayload
{
    private const string KindKey = "kind";
    private const string SessionKey = "session";

    public static JsonObject Create(DeletionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var payload = new JsonObject { [KindKey] = Name(target.Kind) };
        if (target.SessionName is { } session)
        {
            payload[SessionKey] = session;
        }

        return payload;
    }

    /// <summary>The target of a deletion item.</summary>
    /// <exception cref="FormatException">
    /// The item is not a deletion item, has no workspace, or its payload is malformed or names another session than the
    /// item's <see cref="WorkItemRecord.SessionName"/> (checked only when that is set).
    /// </exception>
    public static DeletionTarget Read(WorkItemRecord item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var kind = String(item.Payload, KindKey);
        var session = String(item.Payload, SessionKey);
        var consistent = item.SessionName is null || string.Equals(item.SessionName, session, StringComparison.Ordinal);
        return (item.TaskType, kind, item.WorkspaceName, session) switch
        {
            (WorkTaskType.Deletion, "session", { } workspace, { } name) when consistent => DeletionTarget.Session(workspace, name),
            (WorkTaskType.Deletion, "workspace", { } workspace, null) when item.SessionName is null => DeletionTarget.Workspace(workspace),
            _ => throw new FormatException($"Work item {item.Id} is not a well-formed deletion item."),
        };
    }

    public static string Name(DeletionTargetKind kind) => kind switch
    {
        DeletionTargetKind.Session => "session",
        DeletionTargetKind.Workspace => "workspace",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown deletion target kind."),
    };

    private static string? String(JsonObject payload, string key) =>
        payload[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}
