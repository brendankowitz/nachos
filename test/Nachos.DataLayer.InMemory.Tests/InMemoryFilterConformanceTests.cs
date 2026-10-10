using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Testing.Filtering;

namespace Nachos.DataLayer.InMemory.Tests;

public sealed class InMemoryFilterConformanceTests : FilterConformanceTests
{
    // The base class seeds once per derived class, so the store must outlive the per-test instances.
    private static readonly InMemoryMemoryStore Store = new(TimeProvider.System);

    // Deliberately small, so every multi-row result is read across several pages.
    private const int PageSize = 3;

    protected override async Task SeedAsync(FilterDataset dataset)
    {
        if (await Store.Workspaces.GetAsync(dataset.Workspaces[0].Name, CancellationToken.None) is not null)
        {
            return;
        }

        var scope = dataset.ScopeWorkspace;
        foreach (var workspace in dataset.Workspaces)
        {
            Store.SeedWorkspace(workspace.Name, workspace.CreatedAt, workspace.Metadata);
        }

        foreach (var peer in dataset.Peers)
        {
            Store.SeedPeer(scope, peer.Name, peer.CreatedAt, peer.Metadata);
        }

        foreach (var session in dataset.Sessions)
        {
            Store.SeedSession(scope, session.Name, session.CreatedAt, session.IsActive, session.Metadata);
            foreach (var member in session.Members)
            {
                Store.SeedMember(scope, session.Name, member.Peer, member.Active);
            }
        }

        foreach (var message in dataset.Messages)
        {
            Store.SeedMessage(
                scope,
                message.Session,
                message.Id,
                message.Peer,
                message.Content,
                message.TokenCount,
                message.CreatedAt,
                message.Metadata);
        }
    }

    protected override async Task<IReadOnlyList<string>> QueryAsync(ResourceKind kind, FilterNode? filter)
    {
        var dataset = FilterCaseLibrary.Dataset;
        var scope = dataset.ScopeWorkspace;
        switch (kind)
        {
            case ResourceKind.Workspace:
                return await CollectAsync((page, ct) => Store.Workspaces.ListAsync(filter, page, ct), w => w.Name);
            case ResourceKind.Peer:
                return await CollectAsync(
                    (page, ct) => Store.Peers.ListAsync(scope, PeerKind.All, filter, page, ct), p => p.Name);
            case ResourceKind.Session:
                return await CollectAsync((page, ct) => Store.Sessions.ListAsync(scope, filter, page, ct), s => s.Name);
            default:
                var ids = new List<string>();
                foreach (var session in dataset.Sessions)
                {
                    ids.AddRange(await CollectAsync(
                        (page, ct) => Store.Messages.ListAsync(scope, session.Name, filter, page, ct), m => m.PublicId));
                }

                return ids;
        }
    }

    private static async Task<List<string>> CollectAsync<T>(
        Func<PageRequest, CancellationToken, Task<Page<T>>> fetch, Func<T, string> id)
    {
        var ids = new List<string>();
        await foreach (var item in fetch.EnumerateAsync(PageSize))
        {
            ids.Add(id(item));
        }

        return ids;
    }
}
