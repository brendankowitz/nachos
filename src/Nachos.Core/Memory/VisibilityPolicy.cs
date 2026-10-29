using System.Collections.Frozen;
using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;

namespace Nachos.Core.Memory;

/// <summary>
/// What one request may read of stored memory (spec §11.8). It is created once per request, and every memory read
/// takes it: there is no read path without one.
/// </summary>
/// <remarks>
/// <para>
/// With a session allowlist, only explicit conclusions stated in an allowlisted session are visible, and peer cards are
/// not: a card aggregates every session, so it cannot be attributed to the allowed ones. An empty allowlist fails
/// closed and shows nothing.
/// </para>
/// <para>Scopes arrive in M6. Until then a request naming one is rejected rather than read without its restriction.</para>
/// </remarks>
public sealed class VisibilityPolicy
{
    public const string ScopeNotSupported = "scope is not supported in this Nachos version";

    private readonly FrozenSet<string>? _sessions;

    private VisibilityPolicy(FrozenSet<string>? sessions)
    {
        _sessions = sessions;
    }

    /// <summary>No restriction: everything the request's target holds is visible.</summary>
    public static VisibilityPolicy Unrestricted { get; } = new(null);

    /// <summary>
    /// The sessions whose explicit conclusions are visible, or null when visibility is not restricted by session.
    /// </summary>
    public IReadOnlySet<string>? SessionAllowlist => _sessions;

    /// <summary>Whether peer cards are visible: only when nothing restricts the request.</summary>
    public bool IncludesPeerCard => _sessions is null;

    /// <param name="sessionAllowlist">The sessions to restrict to, or null for no restriction. Empty shows nothing.</param>
    /// <param name="scopes">The scopes the request names, or null. Any scope is rejected in this version.</param>
    /// <exception cref="NachosValidationException"><paramref name="scopes"/> is not null.</exception>
    public static VisibilityPolicy Create(IReadOnlyCollection<string>? sessionAllowlist, IReadOnlyCollection<string>? scopes)
    {
        if (scopes is not null)
        {
            throw new NachosValidationException(ScopeNotSupported);
        }

        return sessionAllowlist is null ? Unrestricted : new(sessionAllowlist.ToFrozenSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// <paramref name="filter"/> narrowed in the store to what this policy shows, so totals and page sizes count only
    /// visible conclusions. Callers still pass every returned conclusion through <see cref="Allows"/> as a second check.
    /// </summary>
    public FilterNode? Restrict(FilterNode? filter)
    {
        if (_sessions is null)
        {
            return filter;
        }

        if (_sessions.Count == 0)
        {
            return new FilterNode.MatchNone();
        }

        var sessions = new JsonArray([.. _sessions.Order(StringComparer.Ordinal).Select(s => JsonValue.Create(s))]);
        FilterNode allowlisted = new FilterNode.Field(FilterColumns.SessionId, FilterOp.In, sessions);
        FilterNode explicitOnly = new FilterNode.Field(FilterColumns.Level, FilterOp.Eq, JsonValue.Create(ConclusionLevelNames.Explicit));
        return new FilterNode.And(filter is null ? [allowlisted, explicitOnly] : [filter, allowlisted, explicitOnly]);
    }

    /// <summary>Whether <paramref name="conclusion"/> is visible under this policy.</summary>
    public bool Allows(ConclusionRecord conclusion)
    {
        ArgumentNullException.ThrowIfNull(conclusion);
        return _sessions is null
            || (conclusion.Level == ConclusionLevel.Explicit
                && conclusion.SessionName is not null
                && _sessions.Contains(conclusion.SessionName));
    }
}
