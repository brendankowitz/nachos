using System.Text.Json;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.DataLayer.SqlServer.Entities;

namespace Nachos.DataLayer.SqlServer.Storage;

/// <summary>Entity-to-record mapping. Every JSON value is parsed from the stored text, so each record gets fresh nodes.</summary>
internal static class Records
{
    /// <summary>The configuration of a membership the store creates without one (a message sender joining).</summary>
    public static readonly string DefaultPeerConfig = Serialize(new SessionPeerConfig());

    public static WorkspaceRecord ToRecord(this WorkspaceEntity workspace) =>
        new(
            workspace.Name,
            SqlJson.FromStorage(workspace.Metadata),
            SqlJson.FromStorage(workspace.Configuration),
            (LifecycleState)workspace.LifecycleState,
            workspace.CreatedAt);

    public static PeerRecord ToRecord(this PeerEntity peer, string workspaceName) =>
        new(
            workspaceName,
            peer.Name,
            SqlJson.FromStorage(peer.Metadata),
            SqlJson.FromStorage(peer.Configuration),
            peer.IsInternal,
            peer.CreatedAt);

    public static SessionRecord ToRecord(this SessionEntity session, string workspaceName) =>
        new(
            workspaceName,
            session.Name,
            (LifecycleState)session.LifecycleState,
            SqlJson.FromStorage(session.Metadata),
            SqlJson.FromStorage(session.Configuration),
            session.CreatedAt);

    public static MessageRecord ToRecord(this MessageEntity message, string workspaceName, string sessionName, string peerName) =>
        new(
            message.PublicId,
            workspaceName,
            sessionName,
            peerName,
            message.Seq,
            message.Content,
            message.TokenCount,
            SqlJson.FromStorage(message.Metadata),
            message.CreatedAt);

    /// <summary>A copy that shares no JSON node with <paramref name="message"/>.</summary>
    public static MessageRecord Copy(this MessageRecord message) =>
        message with { Metadata = SqlJson.FromStorage(message.Metadata.ToJsonString()) };

    /// <summary>Membership configuration as stored: the wire shape, nulls included.</summary>
    public static string Serialize(SessionPeerConfig config) => JsonSerializer.Serialize(config);

    public static SessionPeerConfig DeserializePeerConfig(string stored) =>
        JsonSerializer.Deserialize<SessionPeerConfig>(stored)
            ?? throw new InvalidOperationException("A stored membership configuration is JSON null.");
}
