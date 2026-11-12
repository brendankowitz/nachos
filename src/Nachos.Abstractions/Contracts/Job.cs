using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>A background job of a workspace, as <c>GET W/jobs</c> lists it (a Nachos extension). In M3 only deletions are jobs.</summary>
/// <param name="Id">The job's work item id, as a string.</param>
/// <param name="Type">The job's kind: <c>deletion</c>.</param>
/// <param name="Target">What the job acts on.</param>
/// <param name="Status">One of <c>pending</c>, <c>in_progress</c>, <c>completed</c> or <c>dead_lettered</c>.</param>
/// <param name="Attempts">How many times the job has been started.</param>
/// <param name="Error">
/// The last failure, or why a completed job did nothing (<c>superseded_by_workspace_deletion</c>); null otherwise.
/// </param>
/// <param name="CreatedAt">When the job was accepted.</param>
/// <param name="CompletedAt">When it completed or was dead-lettered; null while it is pending or in progress.</param>
public sealed record Job(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("target")] JobTarget Target,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("attempts")] int Attempts,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt);
