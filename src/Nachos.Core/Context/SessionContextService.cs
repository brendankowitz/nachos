using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Core.Memory;
using Nachos.Core.Tokens;

namespace Nachos.Core.Context;

/// <summary>
/// Session context within a hard token budget (spec §11.6), and the session's summaries. Every read takes the
/// request's <see cref="VisibilityPolicy"/>; inputs are trusted (the caller validated them).
/// </summary>
/// <remarks>
/// <para>
/// The budget counts content tokens with the registered <see cref="ITokenCounter"/> (M3 D5), and the context is
/// assembled in this order:
/// </para>
/// <list type="number">
/// <item>The card and representation, when requested, share half the budget. A card over half is dropped; the
/// representation is cut to its best-ranked conclusions until both fit.</item>
/// <item>The long summary when it fits in 40% of what is left, else the short one, else none.</item>
/// <item>The newest messages after the summary's coverage, each whole, until the next would not fit.</item>
/// <item>A final count of the assembled content. Over budget means a bug in the steps above: it is logged as an error and
/// content is dropped (oldest messages first) until the context fits.</item>
/// </list>
/// <para>
/// Messages and summaries belong to one session, so they are visible only when the policy shows that session as a
/// whole: an allowlist that does not name it hides them.
/// </para>
/// </remarks>
public sealed partial class SessionContextService(
    IMemoryStore store,
    PeerContextService peerContext,
    ITokenCounter tokenCounter,
    ILogger<SessionContextService>? logger = null,
    MessageChunker? chunker = null)
{
    // Long first: it covers more of the session for its tokens.
    private static readonly SummaryKind[] PreferredSummaries = [SummaryKind.Long, SummaryKind.Short];

    private readonly ILogger _logger = logger ?? NullLogger<SessionContextService>.Instance;

    /// <exception cref="NotFoundException">The workspace or the session does not exist.</exception>
    /// <exception cref="ServiceUnavailableException">The representation's search query could not be embedded.</exception>
    public async Task<AssembledContext> GetContextAsync(
        string workspaceName, string sessionName, SessionContextRequest request, VisibilityPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        _ = await store.Sessions.GetAsync(workspaceName, sessionName, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("Session not found.");

        var budget = new ContextBudget(request.Tokens);
        string? representation = null;
        IReadOnlyList<string>? card = null;
        if (request.Representation is { } peers)
        {
            (representation, card) = await ReservePeersAsync(workspaceName, peers, policy, budget, ct).ConfigureAwait(false);
        }

        var summary = request.IncludeSummary
            ? await ReserveSummaryAsync(workspaceName, sessionName, policy, budget, ct).ConfigureAwait(false)
            : null;
        var messages = await ReserveMessagesAsync(
            workspaceName, sessionName, summary?.CoversThroughSeq ?? 0, policy, budget, ct).ConfigureAwait(false);
        return Enforce(new AssembledContext(messages, summary, representation, card), request.Tokens);
    }

    /// <summary>The session's current summaries, short before long; empty when the policy hides the session.</summary>
    /// <exception cref="NotFoundException">The workspace or the session does not exist.</exception>
    public async Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(
        string workspaceName, string sessionName, VisibilityPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var summaries = await store.Summaries.ListAsync(workspaceName, sessionName, ct).ConfigureAwait(false);
        return policy.AllowsSession(sessionName) ? summaries : [];
    }

    private async Task<(string Representation, IReadOnlyList<string>? Card)> ReservePeersAsync(
        string workspaceName, ContextRepresentation peers, VisibilityPolicy policy, ContextBudget budget, CancellationToken ct)
    {
        var card = await peerContext.GetCardAsync(workspaceName, peers.ObserverName, peers.ObservedName, policy, ct)
            .ConfigureAwait(false);
        var cardTokens = card?.Sum(tokenCounter.Count) ?? 0;
        if (cardTokens > budget.PeerShare)
        {
            card = null;
            cardTokens = 0;
        }

        var (representation, representationTokens) = await FitRepresentationAsync(
            workspaceName, peers, policy, budget.PeerShare - cardTokens, ct).ConfigureAwait(false);
        Reserve(budget, cardTokens + representationTokens);
        return (representation, card);
    }

    /// <summary>
    /// The representation with as many of its best-ranked conclusions as fit in <paramref name="limit"/> tokens.
    /// <see cref="RepresentationBuilder"/> keeps the best-ranked conclusions up to <see cref="RepresentationQuery.MaxConclusions"/>,
    /// so building with a smaller maximum drops the lowest-ranked ones; the largest maximum that fits is found by
    /// bisection, rebuilding at most about log2(MaxConclusions) times, and only when the whole one does not fit.
    /// </summary>
    private async Task<(string Text, int Tokens)> FitRepresentationAsync(
        string workspaceName, ContextRepresentation peers, VisibilityPolicy policy, int limit, CancellationToken ct)
    {
        var query = peers.Query with { SearchQuery = EmbeddableText(peers.Query.SearchQuery) };
        var whole = await BuildAsync(query.MaxConclusions).ConfigureAwait(false);
        if (whole.Tokens <= limit)
        {
            return whole;
        }

        (string Text, int Tokens) best = ("", 0);
        var fits = 0;
        var fails = query.MaxConclusions;
        while (fails - fits > 1)
        {
            var size = fits + (fails - fits) / 2;
            var candidate = await BuildAsync(size).ConfigureAwait(false);
            if (candidate.Tokens <= limit)
            {
                (fits, best) = (size, candidate);
            }
            else
            {
                fails = size;
            }
        }

        return best;

        async Task<(string Text, int Tokens)> BuildAsync(int maxConclusions)
        {
            var text = await peerContext.GetRepresentationAsync(workspaceName, peers.ObserverName, peers.ObservedName, policy,
                query with { MaxConclusions = maxConclusions }, ct).ConfigureAwait(false);
            return (text, tokenCounter.Count(text));
        }
    }

    /// <summary>
    /// The search query as it is embedded: only its first chunk when the embedding model's chunker is registered, because
    /// the model rejects a longer input as a client error, which no retry can fix.
    /// </summary>
    private string? EmbeddableText(string? searchQuery) =>
        searchQuery is not null && chunker?.Chunk(searchQuery) is [var first, ..] ? first : searchQuery;

    private async Task<SummaryRecord?> ReserveSummaryAsync(
        string workspaceName, string sessionName, VisibilityPolicy policy, ContextBudget budget, CancellationToken ct)
    {
        if (!policy.AllowsSession(sessionName))
        {
            return null;
        }

        var summaries = await store.Summaries.ListAsync(workspaceName, sessionName, ct).ConfigureAwait(false);
        var share = budget.SummaryShare;
        foreach (var kind in PreferredSummaries)
        {
            var summary = summaries.FirstOrDefault(s => s.Kind == kind);
            var tokens = summary is null ? 0 : tokenCounter.Count(summary.Content);
            if (summary is not null && tokens <= share)
            {
                Reserve(budget, tokens);
                return summary;
            }
        }

        return null;
    }

    /// <summary>
    /// The newest messages after <paramref name="coveredThroughSeq"/> that fit, oldest first. Reads newest first, a page at
    /// a time, and stops at the first message that does not fit: the context is an unbroken stretch ending at the newest
    /// message. Older pages are read by <c>Seq</c> range, so a concurrent append cannot shift them.
    /// </summary>
    private async Task<List<MessageRecord>> ReserveMessagesAsync(
        string workspaceName,
        string sessionName,
        long coveredThroughSeq,
        VisibilityPolicy policy,
        ContextBudget budget,
        CancellationToken ct)
    {
        const int PageSize = PageRequest.MaxSize;
        var taken = new List<MessageRecord>();
        if (!policy.AllowsSession(sessionName))
        {
            return taken;
        }

        var newest = await store.Messages.ListAsync(workspaceName, sessionName, null, new PageRequest(Size: PageSize, Reverse: true), ct)
            .ConfigureAwait(false);
        if (TakeAll(newest.Items) && newest.Items.Count > 0)
        {
            for (var upper = newest.Items[^1].Seq - 1; upper > coveredThroughSeq; upper -= PageSize)
            {
                var lower = Math.Max(coveredThroughSeq + 1, upper - PageSize + 1);
                var older = await store.Messages.ListRangeAsync(workspaceName, sessionName, lower, upper, ct).ConfigureAwait(false);
                if (!TakeAll([.. older.Reverse()]))
                {
                    break;
                }
            }
        }

        taken.Reverse();
        return taken;

        bool TakeAll(IReadOnlyList<MessageRecord> newestFirst)
        {
            foreach (var message in newestFirst)
            {
                if (message.Seq <= coveredThroughSeq || !budget.TryReserve(tokenCounter.Count(message.Content)))
                {
                    return false;
                }

                taken.Add(message);
            }

            return true;
        }
    }

    private AssembledContext Enforce(AssembledContext context, int tokens)
    {
        var total = context.ContentTokens(tokenCounter);
        if (total <= tokens)
        {
            return context;
        }

        LogOverBudget(_logger, total, tokens);
        var messageTokens = context.Messages.Select(m => tokenCounter.Count(m.Content)).ToArray();
        var dropped = 0;
        for (; dropped < messageTokens.Length && total > tokens; dropped++)
        {
            total -= messageTokens[dropped];
        }

        context = context with { Messages = [.. context.Messages.Skip(dropped)] };
        if (total > tokens)
        {
            context = context with { Summary = null };
        }

        if (context.ContentTokens(tokenCounter) > tokens)
        {
            context = context with { PeerCard = null };
        }

        return context.ContentTokens(tokenCounter) > tokens ? context with { Representation = "" } : context;
    }

    private static void Reserve(ContextBudget budget, int tokens)
    {
        if (!budget.TryReserve(tokens))
        {
            throw new InvalidOperationException(
                $"The session context reserved {tokens} tokens with {budget.Remaining} left; each step must reserve only what fits.");
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A session context counted {Total} content tokens against a budget of {Budget} at its final check; content was dropped to fit. The budget steps or the token counter are inconsistent.")]
    private static partial void LogOverBudget(ILogger logger, int total, int budget);
}
