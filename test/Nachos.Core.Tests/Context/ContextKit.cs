using Microsoft.Extensions.Logging;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Core.Context;
using Nachos.Core.Memory;
using Nachos.Core.Tests.Memory;
using Nachos.Core.Tokens;
using Shouldly;

namespace Nachos.Core.Tests.Context;

/// <summary>One token per whitespace-separated word, so a test can size content exactly.</summary>
internal sealed class WordCounter : ITokenCounter
{
    public int Count(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

/// <summary>Builders shared by the session context tests. Content sizes are in <see cref="WordCounter"/> tokens.</summary>
internal static class ContextKit
{
    private static readonly WordCounter Counter = new();

    /// <summary><paramref name="count"/> words: <paramref name="label"/> followed by filler.</summary>
    public static string Words(int count, string label = "w") =>
        count < 1 ? "" : string.Join(' ', [label, .. Enumerable.Repeat("w", count - 1)]);

    public static SessionContextRequest Request(int tokens, bool summary = true, ContextRepresentation? representation = null) =>
        new(tokens, summary, representation);

    public static ContextRepresentation Peers(string observer = "bob", string observed = "alice", RepresentationQuery? query = null) =>
        new(observer, observed, query ?? new RepresentationQuery());

    /// <summary>Appends <paramref name="contents"/> from <paramref name="peer"/> in one batch, a minute apart from the last batch.</summary>
    public static async Task<IReadOnlyList<MessageRecord>> AppendAsync(
        MemoryFixture f, string session, IEnumerable<string> contents, string peer = "alice", SummaryIntent? summary = null)
    {
        NewMessage[] messages = [.. contents.Select(c => new NewMessage(peer, c, Counter.Count(c), null, f.Clock.GetUtcNow()))];
        var stored = await f.Store.Messages.AppendAsync(MemoryFixture.W, session, new MessageAppend(messages, Summary: summary), default);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        return stored;
    }

    /// <summary>
    /// Appends <paramref name="covered"/> to an empty session and saves summaries covering through its last message, as
    /// the summarizer would; then appends <paramref name="after"/>. Returns every message, oldest first.
    /// </summary>
    public static async Task<IReadOnlyList<MessageRecord>> SummarizedSessionAsync(
        MemoryFixture f,
        string session,
        IReadOnlyList<string> covered,
        IReadOnlyList<string> after,
        string? shortSummary,
        string? longSummary,
        string peer = "alice")
    {
        var messages = new List<MessageRecord>();
        if (covered.Count > 0)
        {
            var due = shortSummary is null && longSummary is null ? null : new SummaryIntent(covered.Count, covered.Count);
            messages.AddRange(await AppendAsync(f, session, covered, peer, due));
            if (due is not null)
            {
                var through = messages[^1];
                var policy = new Dictionary<WorkTaskType, WorkBatchPolicy> { [WorkTaskType.Summary] = WorkBatchPolicy.Immediate };
                var lease = (await f.Store.WorkQueue.TryAcquireAsync("test", policy, TimeSpan.FromMinutes(5), default))
                    .ShouldNotBeNull();
                var summaries = new List<NewSummary>();
                if (shortSummary is not null)
                {
                    summaries.Add(new(SummaryKind.Short, shortSummary, through.PublicId, through.Seq, Counter.Count(shortSummary)));
                }

                if (longSummary is not null)
                {
                    summaries.Add(new(SummaryKind.Long, longSummary, through.PublicId, through.Seq, Counter.Count(longSummary)));
                }

                await f.Store.Summaries.SaveAsync(lease, MemoryFixture.W, session, summaries, default);
            }
        }

        if (after.Count > 0)
        {
            messages.AddRange(await AppendAsync(f, session, after, peer));
        }

        return messages;
    }

    /// <summary>The content tokens of <paramref name="context"/> (D5), counted here independently of the service.</summary>
    public static int ContentTokens(AssembledContext context) =>
        Counter.Count(context.Representation ?? "")
        + (context.PeerCard?.Sum(Counter.Count) ?? 0)
        + Counter.Count(context.Summary?.Content ?? "")
        + context.Messages.Sum(m => Counter.Count(m.Content));

    /// <summary>The same count over the wire shape.</summary>
    public static int ContentTokens(SessionContext context) =>
        Counter.Count(context.PeerRepresentation ?? "")
        + (context.PeerCard?.Sum(Counter.Count) ?? 0)
        + Counter.Count(context.Summary?.Content ?? "")
        + context.Messages.Sum(m => Counter.Count(m.Content));

    public static void ShouldNotHaveTrippedTheGuard(this MemoryFixture f) => f.Logs.At(LogLevel.Error).ShouldBeEmpty();
}
