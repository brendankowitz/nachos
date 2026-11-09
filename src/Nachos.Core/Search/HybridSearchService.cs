using Microsoft.Extensions.AI;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Core.Memory;

namespace Nachos.Core.Search;

/// <summary>
/// Message search (spec §11.5): vector and lexical candidates retrieved concurrently and fused with
/// <see cref="ReciprocalRankFusion"/>. Every search takes the request's <see cref="VisibilityPolicy"/>; inputs are
/// trusted (the caller validated them).
/// </summary>
/// <remarks>
/// The embedding generator is optional: without one, search is lexical only rather than unavailable, because the lexical
/// half alone is useful. The host warns about the missing generator once, at startup. The chunker, registered with the
/// generator, caps what is embedded: only the query's first chunk, because the model rejects a longer input as a client
/// error, which no retry can fix.
/// </remarks>
public sealed class HybridSearchService(
    IMemoryStore store,
    IEmbeddingGenerator<string, Embedding<float>>? embeddings = null,
    MessageChunker? chunker = null)
{
    /// <summary>How many candidates each method retrieves per requested result.</summary>
    public const int CandidatesPerResult = 4;

    /// <summary>
    /// The <paramref name="limit"/> best visible messages of <paramref name="request"/> for <paramref name="query"/>. A
    /// blank query finds nothing and calls nothing. A query with no word (<see cref="LexicalQuery.Terms"/>) skips lexical
    /// retrieval. Lexical retrieval reads the whole query; vector retrieval embeds only its first chunk when there is a
    /// chunker.
    /// </summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    /// <exception cref="ServiceUnavailableException">The embedding service failed transiently.</exception>
    public async Task<IReadOnlyList<MessageRecord>> SearchAsync(
        MessageSearchRequest request, string query, int limit, VisibilityPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var visible = request with { Filter = policy.RestrictMessages(request.Filter) };
        var take = CandidatesPerResult * limit;
        var terms = LexicalQuery.Terms(query);
        var vector = VectorAsync();
        var lexical = terms.Count == 0
            ? Task.FromResult<IReadOnlyList<MessageHit>>([])
            : store.Search.LexicalAsync(visible, terms, take, ct);
        await Task.WhenAll(vector, lexical).ConfigureAwait(false);

        var fused = ReciprocalRankFusion.Fuse([await vector.ConfigureAwait(false), await lexical.ConfigureAwait(false)]);
        return [.. fused.Where(policy.Allows).Take(limit)];

        async Task<IReadOnlyList<MessageHit>> VectorAsync()
        {
            if (embeddings is null)
            {
                return [];
            }

            var text = chunker is null ? query : chunker.Chunk(query)[0];
            var embedded = await QueryEmbedding.EmbedAsync(embeddings, text, ct).ConfigureAwait(false);
            return await store.Search.VectorAsync(visible, embedded, take, ct).ConfigureAwait(false);
        }
    }
}
