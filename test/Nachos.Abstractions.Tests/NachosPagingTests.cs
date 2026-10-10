using Nachos.Abstractions.Contracts;
using Shouldly;

namespace Nachos.Abstractions.Tests;

public sealed class NachosPagingTests
{
    [Fact]
    public async Task EnumerateAsync_WalksAllPages()
    {
        var source = Enumerable.Range(1, 5).ToList();
        var requests = new List<PageRequest>();

        Task<Page<int>> Fetch(PageRequest request, CancellationToken ct)
        {
            requests.Add(request);
            var items = source.Skip((request.Page - 1) * request.Size).Take(request.Size).ToList();
            return Task.FromResult(new Page<int>(items, source.Count, request.Page, request.Size, (source.Count + request.Size - 1) / request.Size));
        }

        var all = new List<int>();
        await foreach (var item in NachosPaging.EnumerateAsync<int>(Fetch, pageSize: 2))
        {
            all.Add(item);
        }

        all.ShouldBe(source);
        requests.Select(r => r.Page).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task EnumerateAsync_EmptyCollection_YieldsNothing()
    {
        Task<Page<int>> Fetch(PageRequest request, CancellationToken ct) =>
            Task.FromResult(new Page<int>([], 0, 1, request.Size, 0));

        var all = new List<int>();
        await foreach (var item in NachosPaging.EnumerateAsync<int>(Fetch))
        {
            all.Add(item);
        }

        all.ShouldBeEmpty();
    }
}