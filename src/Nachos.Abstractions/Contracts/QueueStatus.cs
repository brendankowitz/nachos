using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>
/// Reasoning queue status. Each "work unit" count is a count of work items: one per message, covering every observer of
/// that message. <paramref name="CompletedWorkUnits"/> includes dead-lettered items, so the three states add up to
/// <paramref name="TotalWorkUnits"/>.
/// </summary>
/// <param name="Sessions">The same counts per session, keyed by session id; only sessions with selected work appear.</param>
public sealed record QueueStatus(
    [property: JsonPropertyName("total_work_units")] long TotalWorkUnits,
    [property: JsonPropertyName("completed_work_units")] long CompletedWorkUnits,
    [property: JsonPropertyName("in_progress_work_units")] long InProgressWorkUnits,
    [property: JsonPropertyName("pending_work_units")] long PendingWorkUnits,
    [property: JsonPropertyName("sessions")] IReadOnlyDictionary<string, SessionQueueStatus>? Sessions);
