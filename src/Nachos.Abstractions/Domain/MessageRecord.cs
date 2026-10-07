using System.Text.Json.Nodes;

namespace Nachos.Abstractions.Domain;

/// <summary>
/// A stored message. <paramref name="Seq"/> is allocated per session and defines list order;
/// <paramref name="PublicId"/> is case-sensitive.
/// </summary>
public sealed record MessageRecord(
    string PublicId,
    string WorkspaceName,
    string SessionName,
    string PeerName,
    long Seq,
    string Content,
    int TokenCount,
    JsonObject Metadata,
    DateTimeOffset CreatedAt);