using Nachos.Abstractions.Domain;

namespace Nachos.Core.Search;

/// <summary>
/// Reciprocal rank fusion (spec §11.5): a message scores <c>Σ 1 / (K + rank)</c> over the lists it appears in, by its
/// <see cref="MessageHit.Rank"/> in each.
/// </summary>
public static class ReciprocalRankFusion
{
    public const int K = 60;

    /// <summary>
    /// Every message of <paramref name="lists"/> once, best score first; ties go to the newest message (<c>CreatedAt</c>,
    /// then <c>Seq</c>, descending) and then to the lowest public id (ordinal). A message's own record is taken from the
    /// first list it appears in.
    /// </summary>
    public static IReadOnlyList<MessageRecord> Fuse(IEnumerable<IReadOnlyList<MessageHit>> lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        var fused = new Dictionary<string, (MessageRecord Message, double Score)>(StringComparer.Ordinal);
        foreach (var hit in lists.SelectMany(list => list))
        {
            var contribution = 1.0 / (K + hit.Rank);
            fused[hit.Message.PublicId] = fused.TryGetValue(hit.Message.PublicId, out var entry)
                ? (entry.Message, entry.Score + contribution)
                : (hit.Message, contribution);
        }

        return
        [
            .. fused.Values
                .OrderByDescending(entry => entry.Score)
                .ThenByDescending(entry => entry.Message.CreatedAt)
                .ThenByDescending(entry => entry.Message.Seq)
                .ThenBy(entry => entry.Message.PublicId, StringComparer.Ordinal)
                .Select(entry => entry.Message),
        ];
    }
}
