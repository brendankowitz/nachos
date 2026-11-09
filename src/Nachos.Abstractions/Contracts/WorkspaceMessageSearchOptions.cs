using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>The body of a workspace message search (<c>POST W/search</c>).</summary>
/// <param name="Query">The text to search for; blank finds nothing.</param>
/// <param name="Limit">How many messages to return at most, 1 to 100.</param>
/// <param name="Filters">A message filter, as in <see cref="MessageSearchOptions.Filters"/>.</param>
/// <param name="Scope">A scope id. Scopes are not supported yet, so any value is a 422.</param>
public sealed record WorkspaceMessageSearchOptions(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("limit")] int Limit = MessageSearchOptions.DefaultLimit,
    [property: JsonPropertyName("filters")] JsonObject? Filters = null,
    [property: JsonPropertyName("scope")] string? Scope = null);
