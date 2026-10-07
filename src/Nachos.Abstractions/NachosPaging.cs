using System.Runtime.CompilerServices;
using Nachos.Abstractions.Contracts;

namespace Nachos.Abstractions;

/// <summary>Helpers for walking paged list operations.</summary>
public static class NachosPaging
{
    /// <summary>
    /// Lazily yields every item of a paged listing, fetching one page at a time until the last page
    /// (or an empty page) is reached.
    /// </summary>
    public static async IAsyncEnumerable<T> EnumerateAsync<T>(
        Func<PageRequest, CancellationToken, Task<Page<T>>> fetch,
        int pageSize = 50,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new PageRequest(1, pageSize);
        while (true)
        {
            var page = await fetch(request, ct).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                yield return item;
            }

            if (page.Items.Count == 0 || page.PageNumber >= page.Pages)
            {
                yield break;
            }

            request = request with { Page = request.Page + 1 };
        }
    }
}