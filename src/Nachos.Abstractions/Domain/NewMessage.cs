using System.Text.Json.Nodes;

namespace Nachos.Abstractions.Domain;

/// <summary>A message to append. A null <paramref name="CreatedAt"/> means "now" on the store's clock.</summary>
public sealed record NewMessage(
    string PeerName,
    string Content,
    int TokenCount,
    JsonObject? Metadata,
    DateTimeOffset? CreatedAt);