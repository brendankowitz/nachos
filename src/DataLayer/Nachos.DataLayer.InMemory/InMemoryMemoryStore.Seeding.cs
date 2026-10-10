using System.Text.Json.Nodes;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.DataLayer.InMemory.Storage;

namespace Nachos.DataLayer.InMemory;

/// <summary>
/// Test-only seeding: stores rows exactly as given, including the values the public store interfaces always derive
/// (<c>CreatedAt</c>, an inactive lifecycle, message public ids, former memberships). Reached via InternalsVisibleTo.
/// </summary>
/// <remarks>
/// Each call inserts one new row and throws if it already exists or its parent is missing. Membership and message
/// calls must name peers that were seeded first; messages of one session are appended in call order, which fixes
/// their <c>Seq</c>. Join and leave times come from the store's clock. Like every entry point, each call rejects a
/// call from inside a <see cref="IdempotencyWrite.SerializeResponse"/> callback.
/// </remarks>
public sealed partial class InMemoryMemoryStore
{
    internal void SeedWorkspace(string name, DateTimeOffset createdAt, JsonObject metadata)
    {
        RejectSerializeResponseReentry();
        lock (_state.Gate)
        {
            var record = new WorkspaceRecord(name, JsonCopy.Own(metadata, "metadata"), new JsonObject(), LifecycleState.Active, createdAt);
            _state.Workspaces.Add(name, new WorkspaceEntry(record, _state.NextOrder()));
        }
    }

    internal void SeedPeer(string workspaceName, string name, DateTimeOffset createdAt, JsonObject metadata)
    {
        RejectSerializeResponseReentry();
        var workspace = _state.RequireWorkspace(workspaceName);
        lock (workspace.Gate)
        {
            var record = new PeerRecord(workspaceName, name, JsonCopy.Own(metadata, "metadata"), new JsonObject(), IsInternal: false, createdAt);
            workspace.Peers.Add(name, new PeerEntry(record, _state.NextOrder()));
        }
    }

    internal void SeedSession(string workspaceName, string name, DateTimeOffset createdAt, bool isActive, JsonObject metadata)
    {
        RejectSerializeResponseReentry();
        var workspace = _state.RequireWorkspace(workspaceName);
        lock (workspace.Gate)
        {
            var state = isActive ? LifecycleState.Active : LifecycleState.Inactive;
            var record = new SessionRecord(workspaceName, name, state, JsonCopy.Own(metadata, "metadata"), new JsonObject(), createdAt);
            workspace.Sessions.Add(name, new SessionEntry(record, _state.NextOrder()));
        }
    }

    /// <param name="active">False seeds a former member: it joined and has since left.</param>
    internal void SeedMember(string workspaceName, string sessionName, string peerName, bool active)
    {
        RejectSerializeResponseReentry();
        var workspace = _state.RequireWorkspace(workspaceName);
        lock (workspace.Gate)
        {
            var session = workspace.RequireSession(sessionName);
            workspace.RequirePeer(peerName);
            var now = _state.Clock.GetUtcNow();
            session.Members.Add(peerName, new Membership(new SessionPeerConfig(), now, active ? null : now));
        }
    }

    internal void SeedMessage(
        string workspaceName,
        string sessionName,
        string publicId,
        string peerName,
        string content,
        int tokenCount,
        DateTimeOffset createdAt,
        JsonObject metadata)
    {
        RejectSerializeResponseReentry();
        var workspace = _state.RequireWorkspace(workspaceName);
        lock (workspace.Gate)
        {
            var session = workspace.RequireSession(sessionName);
            workspace.RequirePeer(peerName);
            session.AppendMessage(new MessageRecord(
                publicId,
                workspaceName,
                sessionName,
                peerName,
                session.LastSeq + 1,
                content,
                tokenCount,
                JsonCopy.Own(metadata, "metadata"),
                createdAt));
        }
    }

    private void RejectSerializeResponseReentry()
    {
        if (_state.SerializeResponseGuard.Reject() is { } rejection)
        {
            throw rejection;
        }
    }
}
