using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions.Stores;

/// <summary>
/// Candidate retrieval for message search, one list per method, each in rank order and fused by the caller. See
/// <see cref="IMemoryStore"/> for the rules shared by all stores.
/// </summary>
/// <remarks>
/// The candidate set is <see cref="MessageSearchRequest.Scope"/> narrowed by <see cref="MessageSearchRequest.Filter"/>,
/// both applied inside the store. Messages of a workspace or session being deleted are never candidates. Both methods throw <see cref="NotFoundException"/> when the workspace does not exist;
/// a scope naming a session or peer that does not exist matches nothing.
/// <para>
/// <see cref="LexicalAsync"/> may be served by a full-text index. While one is active, a term matches words and word
/// prefixes ("lemon" matches "lemonade" but not "sublemon"), ranked by the index's relevance; the substring definition below
/// applies otherwise, and may return a superset. A term inside a word, and text in a script written without spaces (such as
/// CJK), is found only by the substring definition.
/// </para>
/// </remarks>
public interface IMessageSearch
{
    /// <summary>
    /// The longest term <see cref="LexicalAsync"/> accepts, in UTF-16 code units: SQL Server cannot search for a string
    /// longer than 4,000 characters, and an escaped substring pattern can be twice the term's length.
    /// </summary>
    const int MaxTermLength = 1000;

    /// <summary>
    /// The <paramref name="take"/> messages nearest to <paramref name="query"/> by exact cosine distance, a message's
    /// distance being that of its nearest embedded chunk; ties go to the newest (<c>CreatedAt</c>, then <c>Seq</c>, then
    /// public id). Messages with no embedded chunk are not candidates.
    /// </summary>
    /// <exception cref="ArgumentException">The vector is not <see cref="VectorSpace.Dimensions"/> long.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="take"/> is less than 1.</exception>
    Task<IReadOnlyList<MessageHit>> VectorAsync(
        MessageSearchRequest request, ReadOnlyMemory<float> query, int take, CancellationToken ct);

    /// <summary>
    /// The <paramref name="take"/> best messages whose content contains every term, compared case-insensitively. Ranked
    /// by how many times the terms occur, then newest first as in <see cref="VectorAsync"/>. No terms match nothing.
    /// </summary>
    /// <param name="terms">Plain words already sanitized by the caller; the store never parses user text.</param>
    /// <exception cref="ArgumentException">A term is empty or longer than <see cref="MaxTermLength"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="take"/> is less than 1.</exception>
    Task<IReadOnlyList<MessageHit>> LexicalAsync(
        MessageSearchRequest request, IReadOnlyList<string> terms, int take, CancellationToken ct);
}
