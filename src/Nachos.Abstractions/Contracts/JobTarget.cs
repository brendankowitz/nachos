using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>What a <see cref="Job"/> acts on.</summary>
/// <param name="Kind"><c>session</c> or <c>workspace</c>.</param>
/// <param name="Id">The session's id, or the workspace's own id.</param>
public sealed record JobTarget(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string Id);
