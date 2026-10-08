using Microsoft.EntityFrameworkCore;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;

namespace Nachos.DataLayer.SqlServer.Storage;

internal static class Paging
{
    /// <summary>
    /// Counts <paramref name="rows"/> and reads one page of them in the order <paramref name="order"/> gives (its second
    /// argument is <see cref="PageRequest.Reverse"/>). A page past the end is empty but still carries the totals, and
    /// costs no second query.
    /// </summary>
    /// <remarks>
    /// The count and the page are two statements, each with its own read-committed-snapshot view, as in Honcho; a
    /// concurrent write can make them disagree by that write.
    /// </remarks>
    public static async Task<Page<T>> ToPageAsync<TEntity, T>(
        IQueryable<TEntity> rows,
        PageRequest request,
        Func<IQueryable<TEntity>, bool, IQueryable<TEntity>> order,
        Func<List<TEntity>, Task<IReadOnlyList<T>>> project,
        CancellationToken ct)
    {
        var total = await rows.LongCountAsync(ct);
        var skip = (long)(request.Page - 1) * request.Size;
        IReadOnlyList<T> items = skip >= total
            ? []
            : await project(await order(rows, request.Reverse).Skip(checked((int)skip)).Take(request.Size).ToListAsync(ct));
        var pages = (int)((total + request.Size - 1) / request.Size);
        return new Page<T>(items, total, request.Page, request.Size, pages);
    }

    /// <summary><see cref="ToPageAsync{TEntity, T}"/> with a synchronous projection.</summary>
    public static Task<Page<T>> ToPageAsync<TEntity, T>(
        IQueryable<TEntity> rows,
        PageRequest request,
        Func<IQueryable<TEntity>, bool, IQueryable<TEntity>> order,
        Func<TEntity, T> project,
        CancellationToken ct) =>
        ToPageAsync(rows, request, order, entities => Task.FromResult<IReadOnlyList<T>>([.. entities.Select(project)]), ct);
}
