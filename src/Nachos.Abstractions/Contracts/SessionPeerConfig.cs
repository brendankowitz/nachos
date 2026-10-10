using System.Text.Json.Serialization;

namespace Nachos.Abstractions.Contracts;

/// <summary>Per-session observation settings for one peer.</summary>
public sealed record SessionPeerConfig(
    [property: JsonPropertyName("observe_me")] bool? ObserveMe = null,
    [property: JsonPropertyName("observe_others")] bool? ObserveOthers = null);