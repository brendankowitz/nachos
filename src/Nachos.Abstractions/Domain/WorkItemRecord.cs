using System.Text.Json.Nodes;

namespace Nachos.Abstractions.Domain;

/// <summary>A claimed work item, as handed out in a <see cref="WorkLease"/>.</summary>
/// <param name="Id">The item's queue position; a unit's items are processed in ascending <paramref name="Id"/> order.</param>
/// <param name="WorkspaceName">The workspace the work belongs to; null for workspace-independent work.</param>
/// <param name="Payload">
/// Task-specific data; for <see cref="WorkTaskType.Representation"/> see <see cref="Work.RepresentationPayload"/>.
/// </param>
/// <param name="Attempts">How many times the item has been leased, this lease included.</param>
/// <param name="MessageId">The public id of the message the item was enqueued for, or null.</param>
public sealed record WorkItemRecord(
    long Id,
    string? WorkspaceName,
    WorkTaskType TaskType,
    string WorkUnitKey,
    JsonObject Payload,
    int Attempts,
    int TokenCount,
    string? SessionName,
    string? MessageId,
    DateTimeOffset CreatedAt);
