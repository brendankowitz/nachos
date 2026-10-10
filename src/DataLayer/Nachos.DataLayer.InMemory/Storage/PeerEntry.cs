using System.Text.Json.Nodes;
using Nachos.Abstractions.Domain;

namespace Nachos.DataLayer.InMemory.Storage;

/// <summary>A stored peer. Its JSON is store-owned: never handed out without a clone.</summary>
internal sealed record PeerEntry(PeerRecord Record, long Order)
{
    /// <summary>A regular peer created implicitly (by a message or a membership change).</summary>
    public static PeerEntry CreateDefault(string workspaceName, string name, DateTimeOffset now, long order) =>
        new(new PeerRecord(workspaceName, name, new JsonObject(), new JsonObject(), IsInternal: false, now), order);
}
