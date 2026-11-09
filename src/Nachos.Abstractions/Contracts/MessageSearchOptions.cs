using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>The body of a session or peer message search (<c>POST S/search</c>, <c>POST P/search</c>).</summary>
/// <param name="Query">The text to search for; blank finds nothing.</param>
/// <param name="Limit">How many messages to return at most, 1 to 100.</param>
/// <param name="Filters">A message filter (<c>id</c>, <c>session_id</c>, <c>peer_id</c>, <c>content</c>, <c>token_count</c>, <c>created_at</c>, <c>metadata</c>).</param>
public sealed record MessageSearchOptions(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("limit")] int Limit = MessageSearchOptions.DefaultLimit,
    [property: JsonPropertyName("filters")] JsonObject? Filters = null)
{
    public const int DefaultLimit = 10;
}
