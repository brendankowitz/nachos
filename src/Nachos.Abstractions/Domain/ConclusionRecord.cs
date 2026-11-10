using System.Text.Json.Nodes;

namespace Nachos.Abstractions.Domain;

/// <summary>
/// A stored, live (not tombstoned) conclusion that <paramref name="ObserverName"/> holds about
/// <paramref name="ObservedName"/>. <paramref name="PublicId"/> is case-sensitive and random (see
/// <see cref="Abstractions.PublicId"/>): the SQL provider enforces it unique across workspaces, the in-memory provider
/// within its workspace, and a collision is astronomically unlikely either way.
/// </summary>
/// <param name="SessionName">The session an explicit conclusion was stated in; null when it is not session-attributed.</param>
/// <param name="TimesDerived">1 on insert; each corroboration adds one.</param>
/// <param name="SourceIds">The public ids of the conclusions this one was reasoned from; empty for explicit ones.</param>
/// <param name="InternalMetadata">
/// Provenance such as the prompt version, never sent on the wire, except <c>source_message_ids</c>, which the wire
/// <c>Conclusion</c> exposes.
/// </param>
public sealed record ConclusionRecord(
    string PublicId,
    string WorkspaceName,
    string ObserverName,
    string ObservedName,
    string? SessionName,
    ConclusionLevel Level,
    string Content,
    int TimesDerived,
    IReadOnlyList<string> SourceIds,
    JsonObject InternalMetadata,
    DateTimeOffset CreatedAt);
