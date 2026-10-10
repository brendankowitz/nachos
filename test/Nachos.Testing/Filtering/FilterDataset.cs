using System.Text.Json.Nodes;

namespace Nachos.Testing.Filtering;

/// <summary>
/// The rows the shared filter cases run against, mirroring the <c>dataset</c> section of
/// <c>filter-cases.json</c>. Every workspace is global; the peers, sessions and messages all live in
/// <see cref="ScopeWorkspace"/>, which is one of <see cref="Workspaces"/>.
/// </summary>
/// <remarks>
/// A provider must store the rows exactly as given, which goes beyond what the public store interfaces allow:
/// it must set <c>CreatedAt</c> on workspaces, peers and sessions, a session's <c>is_active</c>, and message public
/// ids. Providers do this through their own seeding path (for example the clock, or direct inserts).
/// </remarks>
public sealed record FilterDataset(
    string ScopeWorkspace,
    IReadOnlyList<DatasetWorkspace> Workspaces,
    IReadOnlyList<DatasetPeer> Peers,
    IReadOnlyList<DatasetSession> Sessions,
    IReadOnlyList<DatasetMessage> Messages);

/// <summary>A workspace; <paramref name="Name"/> is its id.</summary>
public sealed record DatasetWorkspace(string Name, DateTimeOffset CreatedAt, JsonObject Metadata);

/// <summary>A regular (non-internal) peer of the scope workspace; <paramref name="Name"/> is its id.</summary>
public sealed record DatasetPeer(string Name, DateTimeOffset CreatedAt, JsonObject Metadata);

/// <summary>
/// A session of the scope workspace; <paramref name="Name"/> is its id. <paramref name="IsActive"/> is false for
/// <c>LifecycleState.Inactive</c>.
/// </summary>
public sealed record DatasetSession(
    string Name,
    DateTimeOffset CreatedAt,
    bool IsActive,
    JsonObject Metadata,
    IReadOnlyList<DatasetMember> Members);

/// <summary>A session membership. A member with <c>Active = false</c> joined and later left.</summary>
public sealed record DatasetMember(string Peer, bool Active);

/// <summary>
/// A message. <paramref name="Id"/> is its public id and <paramref name="Session"/> and <paramref name="Peer"/> name
/// its session and sender (always an active member). Messages of one session are appended in dataset order.
/// </summary>
public sealed record DatasetMessage(
    string Id,
    string Session,
    string Peer,
    string Content,
    int TokenCount,
    DateTimeOffset CreatedAt,
    JsonObject Metadata);
