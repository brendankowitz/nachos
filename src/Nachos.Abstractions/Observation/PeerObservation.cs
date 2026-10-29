using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions.Contracts;

namespace Nachos.Abstractions.Observation;

/// <summary>A message sender's <c>observe_me</c> settings; null means "not set" (the default, true).</summary>
/// <param name="PeerObserveMe">The peer-level <c>observe_me</c> (peer configuration).</param>
/// <param name="SessionObserveMe">The session-level <c>observe_me</c> (the sender's membership configuration).</param>
public sealed record PeerObservation(string PeerName, bool? PeerObserveMe, bool? SessionObserveMe)
{
    /// <summary>
    /// Reads the peer-level setting from the peer's stored configuration: only a JSON boolean <c>observe_me</c> counts,
    /// anything else is "not set".
    /// </summary>
    public static PeerObservation From(string peerName, JsonObject peerConfiguration, SessionPeerConfig membership)
    {
        ArgumentNullException.ThrowIfNull(peerConfiguration);
        ArgumentNullException.ThrowIfNull(membership);

        bool? peerObserveMe = peerConfiguration.TryGetPropertyValue("observe_me", out var node)
            && node is JsonValue value
            && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                ? value.GetValueKind() == JsonValueKind.True
                : null;
        return new PeerObservation(peerName, peerObserveMe, membership.ObserveMe);
    }
}
