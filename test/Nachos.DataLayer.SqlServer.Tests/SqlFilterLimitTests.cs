using System.Globalization;
using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.InMemory;
using Nachos.DataLayer.SqlServer.Filtering;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// Filters with thousands of conditions (partner review round 2, I-1): conditions repeated across keys share one test,
/// so the reviewer's 3000- to 10,000-condition filters run and match the in-memory provider; a filter SQL Server still
/// cannot compile is a validation error with a fixed message (422), never a 500.
/// </summary>
[Collection(SqlServerDockerGroup.Name)]
public sealed class SqlFilterLimitTests(SqlServerFixture fixture)
{
    private const string Workspace = "limits";
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly InMemoryMemoryStore Reference = new(TimeProvider.System);
    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static bool _seeded;

    private static readonly string[] Metadata =
    [
        "{}", "{\"c5\":\"v1x\",\"k7\":1}", "{\"c5\":\"v2\",\"k7\":2}", "{\"c9\":{\"x\":\"v1\"},\"k9999\":1}", "{\"c2999\":[\"v1\"],\"k3\":1.0}",
        "{\"c9\":{\"x\":\"V1\"},\"c10\":{\"y\":\"v1\"}}", "{\"k0\":\"1\",\"c0\":1}",
    ];

    private static string I(int i) => i.ToString(CultureInfo.InvariantCulture);

    private static string Of(string combinator, int count, Func<int, string> condition) =>
        "{\"" + combinator + "\":[" + string.Join(",", Enumerable.Range(0, count).Select(condition)) + "]}";

    public static TheoryData<string, string> WideFilters() => new()
    {
        { "3000 contains of one operand on distinct keys", Of("OR", 3000, i => "{\"metadata\":{\"c" + I(i) + "\":{\"contains\":\"v1\"}}}") },
        { "10000 equalities on distinct keys", Of("OR", 10_000, i => "{\"metadata\":{\"k" + I(i) + "\":1}}") },
        { "10000 ne on distinct keys", Of("AND", 10_000, i => "{\"metadata\":{\"k" + I(i) + "\":{\"ne\":1}}}") },
        { "3000 nested contains on distinct keys", Of("OR", 3000, i => "{\"metadata\":{\"c" + I(i) + "\":{\"x\":{\"icontains\":\"v1\"}}}}") },
    };

    [Theory]
    [MemberData(nameof(WideFilters))]
    public async Task WideFiltersOfSharedConditions_MatchTheReference(string shape, string filterJson)
    {
        var store = await SeedAsync();
        var filter = FilterParser.Parse(filterJson, ResourceKind.Peer);

        (await NamesAsync(store, filter)).ShouldBe(await NamesAsync(Reference, filter), ignoreOrder: true, shape);
    }

    [Fact]
    public async Task FilterSqlServerCannotCompile_IsAValidationError()
    {
        // 1750 ANDs of two conditions on their own keys (one operand, so few parameters): nothing to merge or share, 3500
        // separate flags. SQL Server stops with
        // an expression-services limit (8632) or runs out of resources (8623, 8621); the store reports it as a 422.
        var store = await SeedAsync();
        var filter = FilterParser.Parse(
            "{\"OR\":[" + string.Join(",", Enumerable.Range(0, 1750).Select(i => "{\"metadata\":{\"k" + I(i) + "\":1,\"c" + I(i) + "\":{\"contains\":\"secret\"}}}")) + "]}",
            ResourceKind.Peer);

        var rejected = await Should.ThrowAsync<NachosValidationException>(() => NamesAsync(store, filter));
        rejected.Detail.ShouldBe(SqlFilterCompiler.TooComplex);
        rejected.Detail.ShouldNotContain("secret");
    }

    private static async Task<List<string>> NamesAsync(IMemoryStore store, FilterNode? filter) =>
        [.. (await store.Peers.ListAsync(Workspace, PeerKind.All, filter, new PageRequest(1, 100), Ct)).Items.Select(peer => peer.Name)];

    private async Task<IMemoryStore> SeedAsync()
    {
        var store = (await SqlTestDatabase.GetAsync(fixture, "filter-limits")).CreateStore(TimeProvider.System);
        await SeedLock.WaitAsync(Ct);
        try
        {
            if (!_seeded)
            {
                foreach (var target in new IMemoryStore[] { Reference, store })
                {
                    await target.Workspaces.GetOrCreateAsync(Workspace, null, null, Ct);
                    for (var i = 0; i < Metadata.Length; i++)
                    {
                        await target.Peers.GetOrCreateAsync(Workspace, $"p{i}", JsonNode.Parse(Metadata[i])!.AsObject(), null, Ct);
                    }
                }

                _seeded = true;
            }
        }
        finally
        {
            SeedLock.Release();
        }

        return store;
    }
}
