using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// Pins how <c>icontains</c> folds non-ASCII text on SQL Server, so any change to it is deliberate.
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

    /// <summary>Stored text, <c>icontains</c> operand, and whether SQL Server matches (the in-memory result in the comment).</summary>
    public static TheoryData<string, string, bool> Cases => new()
    {
        { "a", "A", true },                    // ASCII: both providers match
        { "A", "a", true },
        { "é", "É", true },                    // Latin-1: both match
        { "É", "é", true },
        { "µ", "Μ", false },                   // U+00B5 micro vs U+039C capital mu: in-memory matches (µ upper-cases to Μ)
        { "Μ", "µ", false },                   // in-memory matches
        { "μ", "µ", false },                   // U+03BC small mu vs micro: in-memory matches
        { "ı", "I", true },                    // U+0131 dotless i: SQL upper-cases it to I; in-memory does not match
        { "I", "ı", true },                    // in-memory does not match
        { "ſ", "S", false },                   // U+017F long s: in-memory matches (ſ upper-cases to S)
        { "S", "ſ", false },                   // in-memory matches
        { "ა", "Ა", false },                   // U+10D0 Mkhedruli an vs U+1C90 Mtavruli: in-memory matches
        { "Ა", "ა", false },                   // in-memory matches
        { "\U00010428", "\U00010400", false }, // Deseret small long i vs capital (supplementary): in-memory matches
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task IContains_FoldsAsSqlServerUpperDoes(string stored, string operand, bool matches)
    {
        var store = (await SqlTestDatabase.GetAsync(fixture, "icontains-folding")).CreateStore(TimeProvider.System);
        var workspace = $"ws-{Guid.NewGuid():N}";
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        await store.Sessions.GetOrCreateAsync(workspace, "s", null, null, null, Ct);
        await store.Peers.GetOrCreateAsync(workspace, "p", new JsonObject { ["t"] = $"x{stored}y", ["tags"] = new JsonArray(stored) }, null, Ct);
        await store.Messages.AppendAsync(workspace, "s", [new NewMessage("p", $"x{stored}y", 1, null, null)], null, Ct);

        async Task<long> PeersAsync(string json) =>
            (await store.Peers.ListAsync(workspace, PeerKind.All, FilterParser.Parse(json, ResourceKind.Peer), new PageRequest(), Ct)).Total;

        var quoted = JsonValue.Create(operand)!.ToJsonString();
        var expected = matches ? 1 : 0;
        (await PeersAsync("{\"metadata\":{\"t\":{\"icontains\":" + quoted + "}}}")).ShouldBe(expected, "metadata string");
        (await PeersAsync("{\"metadata\":{\"tags\":{\"icontains\":" + quoted + "}}}")).ShouldBe(expected, "metadata array element");
        (await store.Messages.ListAsync(workspace, "s", FilterParser.Parse("{\"content\":{\"icontains\":" + quoted + "}}", ResourceKind.Message), new PageRequest(), Ct))
            .Total.ShouldBe(expected, "content");

        // The exact fallback for operands too long for LIKE folds the same way.
        var padding = new string('a', 4100);
        await store.Messages.AppendAsync(workspace, "s", [new NewMessage("p", padding + stored, 1, null, null)], null, Ct);
        var longOperand = JsonValue.Create(padding.ToUpperInvariant() + operand)!.ToJsonString();
        (await store.Messages.ListAsync(workspace, "s", FilterParser.Parse("{\"content\":{\"icontains\":" + longOperand + "}}", ResourceKind.Message), new PageRequest(), Ct))
            .Total.ShouldBe(expected, "content, long operand");
    }
}
