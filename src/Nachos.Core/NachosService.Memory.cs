using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Core.Memory;
using Nachos.Core.Validation;

namespace Nachos.Core;

/// <summary>
/// The M2 memory routes. Each read builds the request's <see cref="VisibilityPolicy"/> from its own inputs; the routes
/// have no session allowlist of their own except <c>P/representation</c>'s <c>session_id</c>.
/// </summary>
public sealed partial class NachosService
{
    private const int MaxConclusionBatch = 100;
    private const int MaxConclusionCharacters = 65535;
    private const int MaxCount = 100;

    public async Task<IReadOnlyList<Conclusion>> CreateConclusionsAsync(string workspaceId,
        IReadOnlyList<ConclusionCreate> conclusions, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"));
        ValidateConclusions(conclusions);
        return [.. (await conclusionService.CreateAsync(workspaceId, conclusions, ct).ConfigureAwait(false)).Select(Map)];
    }

    public async Task<Page<Conclusion>> ListConclusionsAsync(string workspaceId, JsonObject? filters, PageRequest page,
        CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"));
        ArgumentNullException.ThrowIfNull(page);
        var filter = FilterParser.Parse(filters, ResourceKind.Conclusion);
        return MapPage(await conclusionService.ListAsync(workspaceId, VisibilityPolicy.Unrestricted, filter, page, ct)
            .ConfigureAwait(false), Map);
    }

    public async Task<IReadOnlyList<Conclusion>> QueryConclusionsAsync(string workspaceId, ConclusionQuery query,
        CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"));
        ArgumentNullException.ThrowIfNull(query);
        if (query.Query is null)
        {
            throw Invalid(["body", "query"], "Input should be a valid string", "string_type");
        }

        CheckCount(query.TopK, "body", "top_k");
        CheckDistance(query.Distance, "body", "distance");
        var filter = FilterParser.Parse(query.Filters, ResourceKind.Conclusion);
        var found = await conclusionService.QueryAsync(workspaceId, VisibilityPolicy.Unrestricted, query.Query, query.TopK,
            query.Distance, filter, ct).ConfigureAwait(false);
        return [.. found.Select(Map)];
    }

    public async Task<Conclusion> GetConclusionAsync(string workspaceId, string conclusionId, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (conclusionId, "conclusion_id"));
        return Map(await conclusionService.GetAsync(workspaceId, VisibilityPolicy.Unrestricted, conclusionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("Conclusion not found."));
    }

    public Task DeleteConclusionAsync(string workspaceId, string conclusionId, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (conclusionId, "conclusion_id"));
        return conclusionService.DeleteAsync(workspaceId, conclusionId, ct);
    }

    public async Task<PeerCardResponse> GetPeerCardAsync(string workspaceId, string peerId, string? target = null,
        CancellationToken ct = default)
    {
        var observed = ValidatePerspective(workspaceId, peerId, target, ct);
        return new(await peerContext.GetCardAsync(workspaceId, peerId, observed, VisibilityPolicy.Unrestricted, ct)
            .ConfigureAwait(false));
    }

    public async Task<PeerCardResponse> SetPeerCardAsync(string workspaceId, string peerId, IReadOnlyList<string> peerCard,
        string? target = null, CancellationToken ct = default)
    {
        var observed = ValidatePerspective(workspaceId, peerId, target, ct);
        if (peerCard is null)
        {
            throw Invalid(["body", "peer_card"], "Input should be a valid list", "list_type");
        }

        var entries = peerCard.ToArray();
        for (var index = 0; index < entries.Length; index++)
        {
            if (entries[index] is null)
            {
                throw Invalid(["body", "peer_card", index], "Input should be a valid string", "string_type");
            }
        }

        var card = await peerContext.SetCardAsync(workspaceId, peerId, observed, entries, ct).ConfigureAwait(false);
        return new(card.Entries);
    }

    public async Task<RepresentationResponse> GetRepresentationAsync(string workspaceId, string peerId,
        PeerRepresentationGet request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var observed = ValidatePerspective(workspaceId, peerId, request.Target, ct);
        if (request.SessionId is not null)
        {
            IdValidator.Validate(request.SessionId, "session_id");
        }

        var query = ReadRepresentationQuery("body", request.MaxConclusions, request.IncludeMostFrequent, request.SearchQuery,
            request.SearchTopK, request.SearchMaxDistance, FilterParser.Parse(request.Filters, ResourceKind.Conclusion));
        var policy = VisibilityPolicy.Create(request.SessionId is null ? null : [request.SessionId], ReadScopes(request.Scope));
        return new(await representations.BuildAsync(workspaceId, peerId, observed, policy, query, ct).ConfigureAwait(false));
    }

    public Task<PeerContext> GetPeerContextAsync(string workspaceId, string peerId, PeerContextQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var observed = ValidatePerspective(workspaceId, peerId, query.Target, ct);
        var representation = ReadRepresentationQuery("query", query.MaxConclusions, query.IncludeMostFrequent,
            query.SearchQuery, query.SearchTopK, query.SearchMaxDistance, filter: null);
        return peerContext.GetContextAsync(workspaceId, peerId, observed, VisibilityPolicy.Unrestricted, representation, ct);
    }

    /// <remarks>
    /// The wire names count "work units", but Nachos queues one work item per message for all of that message's
    /// observers, and these counts are of items, one to one. Dead-lettered items count as completed: no further work
    /// will happen on them, and the three wire states then add up to the total. Whether Honcho counts per message or
    /// per observer is an open parity question (spec §9.3).
    /// </remarks>
    public async Task<QueueStatus> GetQueueStatusAsync(string workspaceId, string? observerId = null,
        string? senderId = null, string? sessionId = null, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"));
        ValidateOptional(observerId, "observer_id");
        ValidateOptional(senderId, "sender_id");
        ValidateOptional(sessionId, "session_id");
        var status = await store.WorkQueue.GetStatusAsync(workspaceId, observerId, senderId, sessionId, ct).ConfigureAwait(false);
        var totals = status.Totals;
        return new(totals.Total, totals.Completed + totals.DeadLettered, totals.InProgress, totals.Pending,
            status.Sessions.ToDictionary(s => s.Key, s => new SessionQueueStatus(s.Key, s.Value.Total,
                s.Value.Completed + s.Value.DeadLettered, s.Value.InProgress, s.Value.Pending), StringComparer.Ordinal));
    }

    private static string ValidatePerspective(string workspaceId, string peerId, string? target, CancellationToken ct)
    {
        Validate(ct, (workspaceId, "workspace_id"), (peerId, "peer_id"));
        ValidateOptional(target, "target");
        return target ?? peerId;
    }

    private static void ValidateOptional(string? id, string name)
    {
        if (id is not null)
        {
            IdValidator.Validate(id, name);
        }
    }

    private static void ValidateConclusions(IReadOnlyList<ConclusionCreate> conclusions)
    {
        if (conclusions is null)
        {
            throw Invalid(["body", "conclusions"], "Conclusions must be an array.", "list_type");
        }

        if (conclusions.Count is < 1 or > MaxConclusionBatch)
        {
            throw Invalid(["body", "conclusions"], $"A batch must contain 1–{MaxConclusionBatch} conclusions.",
                conclusions.Count < 1 ? "too_short" : "too_long");
        }

        for (var index = 0; index < conclusions.Count; index++)
        {
            var conclusion = conclusions[index]
                ?? throw Invalid(["body", "conclusions", index], "A conclusion must be an object.", "model_type");
            CheckContent(conclusion.Content, index);
            IdValidator.Validate(conclusion.ObserverId, "observer_id");
            IdValidator.Validate(conclusion.ObservedId, "observed_id");
            ValidateOptional(conclusion.SessionId, "session_id");
        }
    }

    private static void CheckContent(string? content, int index)
    {
        if (content is null)
        {
            throw Invalid(["body", "conclusions", index, "content"], "Content must be a string.", "string_type");
        }

        if (content.Length == 0)
        {
            throw Invalid(["body", "conclusions", index, "content"], "String should have at least 1 character",
                "string_too_short");
        }

        var characters = 0;
        foreach (var _ in content.EnumerateRunes())
        {
            // OpenAPI string lengths count Unicode characters, not UTF-16 code units.
            if (++characters > MaxConclusionCharacters)
            {
                throw Invalid(["body", "conclusions", index, "content"],
                    $"String should have at most {MaxConclusionCharacters} characters", "string_too_long");
            }
        }
    }

    private static RepresentationQuery ReadRepresentationQuery(string source, int? maxConclusions, bool? includeMostFrequent,
        string? searchQuery, int? searchTopK, double? searchMaxDistance, FilterNode? filter)
    {
        CheckCount(maxConclusions, source, "max_conclusions");
        CheckCount(searchTopK, source, "search_top_k");
        CheckDistance(searchMaxDistance, source, "search_max_distance");
        return new(maxConclusions ?? RepresentationQuery.DefaultMaxConclusions, includeMostFrequent ?? false, searchQuery,
            searchTopK ?? RepresentationQuery.DefaultSearchTopK, searchMaxDistance, filter);
    }

    private static void CheckCount(int? value, string source, string field)
    {
        if (value < 1)
        {
            throw Invalid([source, field], "Input should be greater than or equal to 1", "greater_than_equal");
        }

        if (value > MaxCount)
        {
            throw Invalid([source, field], $"Input should be less than or equal to {MaxCount}", "less_than_equal");
        }
    }

    private static void CheckDistance(double? value, string source, string field)
    {
        if (value is null)
        {
            return;
        }

        if (!double.IsFinite(value.Value))
        {
            throw Invalid([source, field], "Input should be a finite number", "finite_number");
        }

        if (value < 0)
        {
            throw Invalid([source, field], "Input should be greater than or equal to 0", "greater_than_equal");
        }

        if (value > 1)
        {
            throw Invalid([source, field], "Input should be less than or equal to 1", "less_than_equal");
        }
    }

    // Any scope is rejected by the policy; the ids are read only so the policy sees what the request named.
    private static string[]? ReadScopes(JsonNode? scope) => scope switch
    {
        null => null,
        JsonArray ids => [.. ids.Select(id => id?.ToJsonString() ?? "null")],
        _ => [scope.ToJsonString()],
    };

    private static RequestValidationException Invalid(IReadOnlyList<object> location, string message, string type) =>
        new([new ValidationError(location, message, type)]);

    private static Conclusion Map(ConclusionRecord row) => new(row.PublicId, row.Content, row.ObserverName,
        row.ObservedName, row.SessionName, WireLevel(row.Level), row.TimesDerived, row.SourceIds, row.CreatedAt);

    private static string WireLevel(ConclusionLevel level) => level switch
    {
        ConclusionLevel.Explicit => "explicit",
        ConclusionLevel.Deductive => "deductive",
        ConclusionLevel.Inductive => "inductive",
        ConclusionLevel.Contradiction => "contradiction",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown conclusion level."),
    };
}
