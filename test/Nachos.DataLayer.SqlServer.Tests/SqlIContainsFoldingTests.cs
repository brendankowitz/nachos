using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.InMemory;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// Pins how <c>icontains</c> folds non-ASCII text on SQL Server <b>and</b> on the in-memory provider, so a change on either
/// side is deliberate.
/// </summary>
/// <remarks>
/// The shared contract (<see cref="FilterOp.IContains"/>) makes folding of non-ASCII text provider-defined and prescribes
/// <c>UPPER()</c> under a binary collation for SQL, which is what this provider does. SQL's <c>UPPER</c> and .NET's
/// <c>ToUpperInvariant</c> (the in-memory provider) differ by design on the characters below, in both directions: SQL does
/// not fold the micro sign, long s, Georgian Mkhedruli or supplementary letters, and does fold dotless ı to I. ASCII and
/// Latin-1 letters such as é fold identically on both providers.
/// </remarks>
[Collection(SqlServerDockerGroup.Name)]
public sealed class SqlIContainsFoldingTests(SqlServerFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>Stored text, <c>icontains</c> operand, whether SQL Server matches, and whether the in-memory provider matches.</summary>
    public static TheoryData<string, string, bool, bool> Cases => new()
    {
        { "a", "A", true, true },                     // ASCII: both providers match
        { "A", "a", true, true },
        { "é", "É", true, true },                     // Latin-1: both match
        { "É", "é", true, true },
        { "µ", "Μ", false, true },                    // U+00B5 micro vs U+039C capital mu: µ upper-cases to Μ in .NET only
        { "Μ", "µ", false, true },
        { "μ", "µ", false, true },                    // U+03BC small mu vs micro: both upper-case to Μ in .NET
        { "ı", "I", true, false },                    // U+0131 dotless i: SQL upper-cases it to I, .NET leaves it
        { "I", "ı", true, false },
        { "ſ", "S", false, true },                    // U+017F long s: ſ upper-cases to S in .NET only
        { "S", "ſ", false, true },
        { "ა", "Ა", false, true },                    // U+10D0 Mkhedruli an vs U+1C90 Mtavruli: .NET folds, SQL does not
        { "Ა", "ა", false, true },
        { "\U00010428", "\U00010400", false, true },  // Deseret small long i vs capital (supplementary): .NET folds
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task IContains_FoldsAsEachProviderDefines(string stored, string operand, bool sqlMatches, bool inMemoryMatches)
    {
        var sql = (await SqlTestDatabase.GetAsync(fixture, "icontains-folding")).CreateStore(TimeProvider.System);
        await AssertFoldingAsync(sql, stored, operand, sqlMatches, "SQL Server");
        await AssertFoldingAsync(new InMemoryMemoryStore(TimeProvider.System), stored, operand, inMemoryMatches, "in-memory");
    }

    private static async Task AssertFoldingAsync(IMemoryStore store, string stored, string operand, bool matches, string provider)
    {
        var workspace = $"ws-{Guid.NewGuid():N}";
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        await store.Sessions.GetOrCreateAsync(workspace, "s", null, null, null, Ct);
        await store.Peers.GetOrCreateAsync(workspace, "p", new JsonObject { ["t"] = $"x{stored}y", ["tags"] = new JsonArray(stored) }, null, Ct);
        await store.Messages.AppendAsync(workspace, "s", [new NewMessage("p", $"x{stored}y", 1, null, null)], null, Ct);

        async Task<long> PeersAsync(string json) =>
            (await store.Peers.ListAsync(workspace, PeerKind.All, FilterParser.Parse(json, ResourceKind.Peer), new PageRequest(), Ct)).Total;

        var quoted = JsonValue.Create(operand)!.ToJsonString();
        var expected = matches ? 1 : 0;
        (await PeersAsync("{\"metadata\":{\"t\":{\"icontains\":" + quoted + "}}}")).ShouldBe(expected, $"{provider}: metadata string");
        (await PeersAsync("{\"metadata\":{\"tags\":{\"icontains\":" + quoted + "}}}")).ShouldBe(expected, $"{provider}: metadata array element");
        (await store.Messages.ListAsync(workspace, "s", FilterParser.Parse("{\"content\":{\"icontains\":" + quoted + "}}", ResourceKind.Message), new PageRequest(), Ct))
            .Total.ShouldBe(expected, $"{provider}: content");

        // SQL's exact fallback for operands too long for LIKE folds the same way.
        var padding = new string('a', 4100);
        await store.Messages.AppendAsync(workspace, "s", [new NewMessage("p", padding + stored, 1, null, null)], null, Ct);
        var longOperand = JsonValue.Create(padding.ToUpperInvariant() + operand)!.ToJsonString();
        (await store.Messages.ListAsync(workspace, "s", FilterParser.Parse("{\"content\":{\"icontains\":" + longOperand + "}}", ResourceKind.Message), new PageRequest(), Ct))
            .Total.ShouldBe(expected, $"{provider}: content, long operand");
    }
}