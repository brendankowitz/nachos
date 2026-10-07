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
/// <see cref="SeedAsync"/> runs before every case on a fresh instance. A provider whose database outlives the test
/// instance either builds an isolated store each time or seeds only once; ids are fixed, so seeding twice into one
/// store would collide. Rows other than the dataset's are ignored, so a shared database is tolerated.
/// </remarks>
public abstract class FilterConformanceTests : IAsyncLifetime
{
    public static IEnumerable<object[]> CaseNames => FilterCaseLibrary.CaseNameData();

    public Task InitializeAsync() => SeedAsync(FilterCaseLibrary.Dataset);

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Stores the dataset exactly as given. See <see cref="FilterDataset"/> for what that requires.</summary>
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
        returned.Where(datasetIds.Contains).ShouldBe(filterCase.Expect!, ignoreOrder: true);
        returned.Count(datasetIds.Contains).ShouldBe(returned.Where(datasetIds.Contains).Distinct().Count(), "duplicate ids");
    }

    private static HashSet<string> DatasetIds(FilterDataset dataset, ResourceKind kind) => (kind switch
    {
        ResourceKind.Workspace => dataset.Workspaces.Select(w => w.Name),
        ResourceKind.Peer => dataset.Peers.Select(p => p.Name),
        ResourceKind.Session => dataset.Sessions.Select(s => s.Name),
        _ => dataset.Messages.Select(m => m.Id),
    }).ToHashSet(StringComparer.Ordinal);
}
