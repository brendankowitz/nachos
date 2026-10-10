using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;

namespace Nachos.DataLayer.InMemory.Storage;

internal static class Paging
{
    /// <summary>
    /// Cuts one page from rows given in ascending list order (reversed first when <see cref="PageRequest.Reverse"/>),
    /// projecting only the rows on the page. A page past the end is empty but still carries the totals.
    /// </summary>
    public static Page<TOut> ToPage<TIn, TOut>(IEnumerable<TIn> ascending, PageRequest request, Func<TIn, TOut> project)
    {
        var rows = ascending.ToList();
        if (request.Reverse)
        {
            rows.Reverse();
        }

        var skip = (long)(request.Page - 1) * request.Size;
        List<TOut> items = skip >= rows.Count ? [] : [.. rows.Skip((int)skip).Take(request.Size).Select(project)];
        var pages = (int)((rows.Count + (long)request.Size - 1) / request.Size);
        return new Page<TOut>(items, rows.Count, request.Page, request.Size, pages);
    }
}
