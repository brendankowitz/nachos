using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>A session summary as exposed on the wire.</summary>
/// <param name="Content">The summary text.</param>
/// <param name="MessageId">The public id of the last message the summary covers.</param>
/// <param name="SummaryType"><c>short</c> or <c>long</c>.</param>
/// <param name="CreatedAt">When the summary was made.</param>
/// <param name="TokenCount">The token count recorded when the summary was made.</param>
public sealed record Summary(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("message_id")] string MessageId,
    [property: JsonPropertyName("summary_type")] string SummaryType,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("token_count")] int TokenCount);
