namespace Nachos.Abstractions.Domain;

/// <summary>A conclusion waiting for its embedding, with the content to embed as it was when it was claimed.</summary>
public sealed record PendingEmbedding(string WorkspaceName, string ConclusionId, string Content);
