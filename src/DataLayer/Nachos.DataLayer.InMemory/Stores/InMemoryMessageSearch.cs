using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.InMemory.Storage;

namespace Nachos.DataLayer.InMemory.Stores;

/// <remarks>Both lists are computed by brute force over the candidate messages, under the workspace gate.</remarks>
internal sealed class InMemoryMessageSearch(InMemoryState state) : IMessageSearch
{
    public Task<IReadOnlyList<MessageHit>> VectorAsync(
        MessageSearchRequest request, ReadOnlyMemory<float> query, int take, int? maxCandidates, CancellationToken ct) =>
        state.Run(
            () =>
            {
                ArgumentNullException.ThrowIfNull(request);
                ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
                if (maxCandidates is { } cap)
                {
                    ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1, nameof(maxCandidates));
                }

                var vector = VectorMath.Own(query, nameof(query));
                return Search(request, take, candidates =>
                {
                    var candidateIds = candidates.Select(c => c.Message.PublicId).ToHashSet(StringComparer.Ordinal);
                    var chunks = candidates.Select(c => c.Session).Distinct()
                        .SelectMany(session => session.Chunks.Values)
                        .Where(chunk => chunk.State == EmbeddingState.Synced && candidateIds.Contains(chunk.MessageId));
                    if (maxCandidates is { } limit)
                    {
                        // The most recently stored chunks, as on SQL Server (highest chunk id).
                        chunks = chunks.OrderByDescending(chunk => chunk.Order).Take(limit);
                    }

                    var distances = chunks
                        .GroupBy(chunk => chunk.MessageId, StringComparer.Ordinal)
                        .ToDictionary(
                            group => group.Key,
                            group => group.Min(chunk => VectorMath.CosineDistance(chunk.Embedding!, vector)),
                            StringComparer.Ordinal);
                    return message => distances.TryGetValue(message.PublicId, out var distance) ? distance : null;
                });
            },
            ct);

    public Task<IReadOnlyList<MessageHit>> LexicalAsync(
        MessageSearchRequest request, IReadOnlyList<string> terms, int take, CancellationToken ct) =>
        state.Run(
            () =>
            {
                ArgumentNullException.ThrowIfNull(request);
                ArgumentNullException.ThrowIfNull(terms);
                ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
                if (terms.Any(term => string.IsNullOrEmpty(term) || term.Length > IMessageSearch.MaxTermLength))
                {
                    throw new ArgumentException(
                        $"A search term must be a non-empty word of at most {IMessageSearch.MaxTermLength} characters.", nameof(terms));
                }

                return Search(request, take, _ => message => Coverage(message.Content, terms));
            },
            ct);

    /// <summary>
    /// The candidates of <paramref name="request"/> that the scoring function scores (lower is better; null leaves a
    /// message out), best first, ties newest first, then by public id.
    /// </summary>
    /// <param name="scoreFor">Builds the scoring function from every candidate, under the workspace gate.</param>
    private IReadOnlyList<MessageHit> Search(
        MessageSearchRequest request,
        int take,
        Func<IReadOnlyList<(SessionEntry Session, MessageRecord Message)>, Func<MessageRecord, double?>> scoreFor)
    {
        var workspace = state.RequireWorkspace(request.WorkspaceName);
        var filter = InMemoryFilterEvaluator.Prepare(request.Filter);
        lock (workspace.Gate)
        {
            List<(SessionEntry Session, MessageRecord Message)> candidates =
            [
                .. Candidates(workspace, request.Scope).SelectMany(candidate => candidate.Session.Messages
                    .Where(m => candidate.Visible(m) && InMemoryFilterEvaluator.Matches(filter, m))
                    .Select(m => (candidate.Session, m))),
            ];
            var score = scoreFor(candidates);
            var scored = new List<(MessageRecord Message, double Score)>();
            foreach (var (_, message) in candidates)
            {
                if (score(message) is { } value)
                {
                    scored.Add((message, value));
                }
            }

            return
            [
                .. scored
                    .OrderBy(hit => hit.Score)
                    .ThenByDescending(hit => hit.Message.CreatedAt)
                    .ThenByDescending(hit => hit.Message.Seq)
                    .ThenBy(hit => hit.Message.PublicId, StringComparer.Ordinal)
                    .Take(take)
                    .Select((hit, index) => new MessageHit(JsonCopy.Out(hit.Message), index + 1)),
            ];
        }
    }

    /// <summary>The sessions in scope, each with the test of which of its messages are in scope.</summary>
    /// <remarks>A workspace or session being deleted has no candidates.</remarks>
    private static IEnumerable<(SessionEntry Session, Func<MessageRecord, bool> Visible)> Candidates(
        WorkspaceEntry workspace, MessageSearchScope scope) =>
        workspace.IsDeleting ? [] : InScope(workspace, scope).Where(candidate => !candidate.Session.IsDeleting);

    private static IEnumerable<(SessionEntry Session, Func<MessageRecord, bool> Visible)> InScope(
        WorkspaceEntry workspace, MessageSearchScope scope) => scope switch
    {
        MessageSearchScope.Workspace => workspace.Sessions.Values.Select(session => (session, (Func<MessageRecord, bool>)(_ => true))),
        MessageSearchScope.Session { SessionName: var name } => workspace.Sessions.TryGetValue(name, out var session)
            ? [(session, _ => true)]
            : [],
        MessageSearchScope.PeerPerspective { PeerName: var peer } => workspace.Sessions.Values
            .Where(session => session.Members.ContainsKey(peer))
            .Select(session =>
            {
                var membership = session.Members[peer];
                return (session, (Func<MessageRecord, bool>)(message =>
                    membership.JoinedAt <= message.CreatedAt && (membership.LeftAt is not { } left || message.CreatedAt < left)));
            }),
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown message search scope."),
    };

    /// <summary>
    /// How many of the distinct (case-insensitively) terms occur in <paramref name="content"/>, case-insensitively, negated
    /// because a lower score ranks first; null when none occurs.
    /// </summary>
    private static double? Coverage(string content, IReadOnlyList<string> terms)
    {
        var matched = terms
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(term => content.Contains(term, StringComparison.OrdinalIgnoreCase));
        return matched == 0 ? null : -matched;
    }
}
