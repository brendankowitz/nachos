using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions.Work;

/// <summary>
/// The payload of a <see cref="WorkTaskType.Deletion"/> item: <c>{"kind": "session"|"workspace"}</c>. The target's names
/// are the item's own <see cref="WorkItemRecord.WorkspaceName"/> and <see cref="WorkItemRecord.SessionName"/>.
/// </summary>
public static class DeletionPayload
{
    private const string KindKey = "kind";

    public static JsonObject Create(DeletionTargetKind kind) => new() { [KindKey] = Name(kind) };

    /// <summary>The target of a deletion item.</summary>
    /// <exception cref="FormatException">
    /// The item is not a deletion item, or its payload's kind does not match its workspace and session.
    /// </exception>
    public static DeletionTarget Read(WorkItemRecord item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var kind = item.Payload[KindKey] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;
        return (item.TaskType, kind, item.WorkspaceName, item.SessionName) switch
        {
            (WorkTaskType.Deletion, "session", { } workspace, { } session) => DeletionTarget.Session(workspace, session),
            (WorkTaskType.Deletion, "workspace", { } workspace, null) => DeletionTarget.Workspace(workspace),
            _ => throw new FormatException($"Work item {item.Id} is not a well-formed deletion item."),
        };
    }

    public static string Name(DeletionTargetKind kind) => kind switch
    {
        DeletionTargetKind.Session => "session",
        DeletionTargetKind.Workspace => "workspace",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown deletion target kind."),
    };
}
