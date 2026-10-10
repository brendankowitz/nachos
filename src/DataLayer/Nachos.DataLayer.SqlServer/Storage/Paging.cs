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
    /// concurrent write can make them disagree by that write. Errors propagate as they are; for rows selected by a compiled
    /// filter, use <see cref="ToFilteredPageAsync{TEntity, T}(IQueryable{TEntity}, PageRequest, Func{IQueryable{TEntity}, bool, IQueryable{TEntity}}, Func{List{TEntity}, Task{IReadOnlyList{T}}}, CancellationToken)"/>.
    /// </remarks>
    public static Task<Page<T>> ToPageAsync<TEntity, T>(
        IQueryable<TEntity> rows,
        PageRequest request,
        Func<IQueryable<TEntity>, bool, IQueryable<TEntity>> order,
        Func<List<TEntity>, Task<IReadOnlyList<T>>> project,
        CancellationToken ct) =>
        PageAsync(rows, request, order, project, filtered: false, ct);

    /// <summary><see cref="ToPageAsync{TEntity, T}(IQueryable{TEntity}, PageRequest, Func{IQueryable{TEntity}, bool, IQueryable{TEntity}}, Func{List{TEntity}, Task{IReadOnlyList{T}}}, CancellationToken)"/> with a synchronous projection.</summary>
    public static Task<Page<T>> ToPageAsync<TEntity, T>(
        IQueryable<TEntity> rows,
        PageRequest request,
        Func<IQueryable<TEntity>, bool, IQueryable<TEntity>> order,
        Func<TEntity, T> project,
        CancellationToken ct) =>
        PageAsync(rows, request, order, Synchronous(project), filtered: false, ct);

    /// <summary>
    /// <see cref="ToPageAsync{TEntity, T}(IQueryable{TEntity}, PageRequest, Func{IQueryable{TEntity}, bool, IQueryable{TEntity}}, Func{List{TEntity}, Task{IReadOnlyList{T}}}, CancellationToken)"/>
    /// for rows selected by a compiled filter (<see cref="Filtering.SqlFilterCompiler"/>): when SQL Server cannot compile
    /// the count or the page statement (<see cref="SqlErrors.IsTooComplex"/>, a filter with very many conditions), the
    /// filter is invalid, a <see cref="NachosValidationException"/> with the fixed detail
    /// <see cref="Filtering.SqlFilterCompiler.TooComplex"/>, like one beyond the parameter limit. Every other failure
    /// (cancellation, a timeout, a permission or any other SQL error) propagates unchanged.
    /// </summary>
    public static Task<Page<T>> ToFilteredPageAsync<TEntity, T>(
        IQueryable<TEntity> rows,
        PageRequest request,
        Func<IQueryable<TEntity>, bool, IQueryable<TEntity>> order,
        Func<List<TEntity>, Task<IReadOnlyList<T>>> project,
        CancellationToken ct) =>
        PageAsync(rows, request, order, project, filtered: true, ct);

    /// <summary><see cref="ToFilteredPageAsync{TEntity, T}(IQueryable{TEntity}, PageRequest, Func{IQueryable{TEntity}, bool, IQueryable{TEntity}}, Func{List{TEntity}, Task{IReadOnlyList{T}}}, CancellationToken)"/> with a synchronous projection.</summary>
    public static Task<Page<T>> ToFilteredPageAsync<TEntity, T>(
        IQueryable<TEntity> rows,
        PageRequest request,
        Func<IQueryable<TEntity>, bool, IQueryable<TEntity>> order,
        Func<TEntity, T> project,
        CancellationToken ct) =>
        PageAsync(rows, request, order, Synchronous(project), filtered: true, ct);

    private static async Task<Page<T>> PageAsync<TEntity, T>(
        IQueryable<TEntity> rows,
        PageRequest request,
        Func<IQueryable<TEntity>, bool, IQueryable<TEntity>> order,
        Func<List<TEntity>, Task<IReadOnlyList<T>>> project,
        bool filtered,
        CancellationToken ct)
    {
        var total = await Run(() => rows.LongCountAsync(ct), filtered);
        var skip = (long)(request.Page - 1) * request.Size;
        IReadOnlyList<T> items = skip >= total
            ? []
            : await project(await Run(() => order(rows, request.Reverse).Skip(checked((int)skip)).Take(request.Size).ToListAsync(ct), filtered));
        var pages = (int)((total + request.Size - 1) / request.Size);
        return new Page<T>(items, total, request.Page, request.Size, pages);
    }

    // Only the statements that hold the compiled filter are translated; the projection's own queries are not.
    private static async Task<TResult> Run<TResult>(Func<Task<TResult>> query, bool filtered)
    {
        try
        {
            return await query();
        }
        catch (Exception ex) when (filtered && SqlErrors.IsTooComplex(ex))
        {
            throw new NachosValidationException(Filtering.SqlFilterCompiler.TooComplex, ex);
        }
    }

    private static Func<List<TEntity>, Task<IReadOnlyList<T>>> Synchronous<TEntity, T>(Func<TEntity, T> project) =>
        entities => Task.FromResult<IReadOnlyList<T>>([.. entities.Select(project)]);
}