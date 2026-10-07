using System.Collections.Concurrent;
using Nachos.Abstractions;
using Nachos.Abstractions.Filtering;
using Shouldly;
using Xunit;

namespace Nachos.Testing.Filtering;

/// <summary>
/// Behavioural contract for a provider's filter compiler. A provider's test project derives from this class,
/// builds <see cref="FilterDataset"/> into its store, and runs filters against it; the cases (see
/// <c>filter-cases.json</c>) pin the semantics documented on <see cref="FilterNode"/> and <see cref="FilterOp"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SeedAsync"/> runs <b>once per derived class</b> (the first case to start triggers it and every other
/// case waits for it), not once per test instance. It therefore must not depend on state owned by one test instance
/// (use static or fixture-owned resources), and it must be <b>idempotent</b>: if the dataset's first workspace
/// already exists, do nothing. Rows outside the dataset are ignored, so a shared database is tolerated.
/// </para>
/// <para>
/// The public store interfaces cannot create the dataset exactly, so a provider seeds through a private path with
/// these operations: set <c>CreatedAt</c> per workspace, peer and session row; set a session's
/// <c>LifecycleState = Inactive</c> where <c>IsActive</c> is false; insert messages with the given public ids (and
/// <c>CreatedAt</c>, token count and metadata), in dataset order within a session so <c>Seq</c> follows it; and
/// insert memberships with <c>LeftAt</c> set for members whose <c>Active</c> is false. For SQL, direct inserts are
/// fine.
/// </para>
/// </remarks>
public abstract class FilterConformanceTests : IAsyncLifetime
{
    private static readonly ConcurrentDictionary<Type, Lazy<Task>> Seeded = new();

    public static IEnumerable<object[]> CaseNames => FilterCaseLibrary.CaseNameData();

    public Task InitializeAsync() =>
        Seeded.GetOrAdd(GetType(), _ => new Lazy<Task>(() => SeedAsync(FilterCaseLibrary.Dataset))).Value;

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Stores the dataset exactly as given, once per derived class, idempotently. See the class remarks for the
    /// operations this needs.
    /// </summary>
    protected abstract Task SeedAsync(FilterDataset dataset);

    /// <summary>
    /// Lists <b>all</b> rows of <paramref name="kind"/> matching <paramref name="filter"/> (null means no filter) and
    /// returns their ids: names for workspaces, peers (<c>PeerKind.All</c>) and sessions of the scope workspace, and
    /// public ids for messages. Messages are listed per session, so run the filter against every dataset session
    /// and return the union. Implementations page through the whole result.
    /// </summary>
    protected abstract Task<IReadOnlyList<string>> QueryAsync(ResourceKind kind, FilterNode? filter);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Case(string name)
    {
        var filterCase = FilterCaseLibrary.Get(name);

        if (filterCase.Error)
        {
            Should.Throw<NachosValidationException>(
                () => FilterParser.Parse(filterCase.Filter, filterCase.Resource));
            return;
        }

        var filter = FilterParser.Parse(filterCase.Filter, filterCase.Resource);
        var returned = await QueryAsync(filterCase.Resource, filter);

        var datasetIds = DatasetIds(FilterCaseLibrary.Dataset, filterCase.Resource);
        var inDataset = returned.Where(datasetIds.Contains).ToList();
        inDataset.ShouldBe(filterCase.Expect!, ignoreOrder: true);
        inDataset.Distinct().Count().ShouldBe(inDataset.Count, "duplicate ids");
    }

    private static HashSet<string> DatasetIds(FilterDataset dataset, ResourceKind kind) => (kind switch
    {
        ResourceKind.Workspace => dataset.Workspaces.Select(w => w.Name),
        ResourceKind.Peer => dataset.Peers.Select(p => p.Name),
        ResourceKind.Session => dataset.Sessions.Select(s => s.Name),
        _ => dataset.Messages.Select(m => m.Id),
    }).ToHashSet(StringComparer.Ordinal);
}
