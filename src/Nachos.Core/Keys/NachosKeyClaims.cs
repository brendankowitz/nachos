namespace Nachos.Core.Keys;

public sealed record NachosKeyClaims(
    bool Admin,
    string? Workspace,
    string? Peer,
    string? Session,
    DateTimeOffset? ExpiresAt);
