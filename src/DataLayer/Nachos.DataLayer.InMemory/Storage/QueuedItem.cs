using System.Text.Json.Nodes;
using Nachos.Abstractions.Domain;

namespace Nachos.DataLayer.InMemory.Storage;

internal enum WorkItemStatus
{
    Pending,
    Failed,
    Succeeded,
    DeadLettered,
}

/// <summary>A stored work item. Mutable; guarded by <see cref="WorkQueueState.Gate"/>.</summary>
/// <param name="Payload">Store-owned: never handed out without a clone.</param>
/// <param name="SenderName">The sender of <paramref name="MessageId"/>, for status filters.</param>
/// <param name="Observers">The payload's observer snapshot, for status filters.</param>
internal sealed class QueuedItem(
    long id,
    string? workspaceName,
    WorkTaskType taskType,
    string workUnitKey,
    JsonObject payload,
    int tokenCount,
    string? sessionName,
    string? messageId,
    string? senderName,
    IReadOnlyList<string> observers,
    DateTimeOffset createdAt)
{
    public long Id { get; } = id;

    public string? WorkspaceName { get; } = workspaceName;

    public WorkTaskType TaskType { get; } = taskType;

    public string WorkUnitKey { get; } = workUnitKey;

    public JsonObject Payload { get; } = payload;

    public int TokenCount { get; } = tokenCount;

    public string? SessionName { get; } = sessionName;

    public string? MessageId { get; } = messageId;

    public string? SenderName { get; } = senderName;

    public IReadOnlyList<string> Observers { get; } = observers;

    public DateTimeOffset CreatedAt { get; } = createdAt;

    public WorkItemStatus Status { get; set; } = WorkItemStatus.Pending;

    public int Attempts { get; set; }

    public DateTimeOffset AvailableAt { get; set; } = createdAt;

    public string? Error { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Pending, or failed and waiting for its retry.</summary>
    public bool IsOpen => Status is WorkItemStatus.Pending or WorkItemStatus.Failed;

    /// <summary>A detached copy for a lease; the payload is cloned.</summary>
    public WorkItemRecord ToRecord() => new(
        Id,
        WorkspaceName,
        TaskType,
        WorkUnitKey,
        JsonNode.Parse(Payload.ToJsonString())!.AsObject(),
        Attempts,
        TokenCount,
        SessionName,
        MessageId,
        CreatedAt);
}
