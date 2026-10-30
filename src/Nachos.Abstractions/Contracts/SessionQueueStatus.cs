using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>One session's reasoning queue counts; see <see cref="QueueStatus"/> for what they count.</summary>
public sealed record SessionQueueStatus(
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("total_work_units")] long TotalWorkUnits,
    [property: JsonPropertyName("completed_work_units")] long CompletedWorkUnits,
    [property: JsonPropertyName("in_progress_work_units")] long InProgressWorkUnits,
    [property: JsonPropertyName("pending_work_units")] long PendingWorkUnits);
