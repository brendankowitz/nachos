using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Shouldly;

namespace Nachos.DataLayer.InMemory.Tests;

/// <summary>
/// Metadata numbers compare exactly through a listed, filtered query: no precision or range bound on any operator.
/// </summary>
public sealed class InMemoryNumericFilterTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private const string Huge = "1e999999999999999999999";

    /// <summary>Workspace name to the JSON literal stored as its <c>n</c> metadata value.</summary>
    private static readonly Dictionary<string, string> Rows = new()
    {
        ["zero"] = "0",
        ["negzero"] = "-0",
        ["tiny"] = "1e-29",
        ["tinier"] = "1e-400",
        ["bigA"] = "79228162514264337593543950336",
        ["bigB"] = "79228162514264337593543950337",
        ["one"] = "1.0",
        ["minus1"] = "-1",
        ["e400"] = "1e400",
        ["e401"] = "1e401",
        ["neg400"] = "-1e400",
        ["neg401"] = "-1e401",
        ["tenth"] = "0.1",
        ["tenthPlus"] = "0.10000000000000000000000000001",
        ["huge"] = Huge,
    };

    /// <summary>The row whose <c>tags</c> array holds numbers that only exact comparison tells apart.</summary>
    private const string ArrayRow = "array";

    private static readonly string[] AllRows = [.. Rows.Keys, ArrayRow];

    public static TheoryData<string, string[]> Filters() => new()
    {
        // eq / in: zero is one value, and tiny values are not zero.
        { """{"n":0}""", ["negzero", "zero"] },
        { """{"n":-0}""", ["negzero", "zero"] },
        { """{"n":0.0e7}""", ["negzero", "zero"] },
        { """{"n":1e-29}""", ["tiny"] },
        { """{"n":1e-400}""", ["tinier"] },
        { """{"n":79228162514264337593543950336}""", ["bigA"] },
        { """{"n":{"in":[79228162514264337593543950337, 1e-400]}}""", ["bigB", "tinier"] },
        { """{"n":{"in":[-0, 1e-401]}}""", ["negzero", "zero"] },

        // One value, many spellings.
        { """{"n":1}""", ["one"] },
        { """{"n":1e0}""", ["one"] },
        { """{"n":10e-1}""", ["one"] },
        { """{"n":0.1e1}""", ["one"] },
        { """{"n":0.1}""", ["tenth"] },
        { """{"n":0.10000000000000000000000000001}""", ["tenthPlus"] },
        { """{"n":1e999999999999999999999}""", ["huge"] },
        { """{"n":10e999999999999999999998}""", ["huge"] },

        // ne: everything else, including the row without the key.
        { """{"n":{"ne":0}}""", [.. AllRows.Except(["zero", "negzero"])] },
        { """{"n":{"ne":79228162514264337593543950337}}""", [.. AllRows.Except(["bigB"])] },

        // Ordering.
        { """{"n":{"gt":0,"lt":1e-20}}""", ["tiny", "tinier"] },
        { """{"n":{"gte":0,"lte":0}}""", ["negzero", "zero"] },
        { """{"n":{"lte":-0}}""", ["minus1", "neg400", "neg401", "negzero", "zero"] },
        { """{"n":{"gt":79228162514264337593543950336}}""", ["bigB", "e400", "e401", "huge"] },
        { """{"n":{"gt":1}}""", ["bigA", "bigB", "e400", "e401", "huge"] },
        { """{"n":{"gte":1,"lt":79228162514264337593543950337}}""", ["bigA", "one"] },
        { """{"n":{"gt":1e400}}""", ["e401", "huge"] },
        { """{"n":{"lt":-1e400}}""", ["neg401"] },
        { """{"n":{"gt":-1e401,"lt":-1e400}}""", [] },
        { """{"n":{"gte":-1e401,"lt":0}}""", ["minus1", "neg400", "neg401"] },
        { """{"n":{"gt":0.1,"lt":1}}""", ["tenthPlus"] },
        { """{"n":{"gt":1e401,"lt":1e999999999999999999999}}""", [] },
        { """{"n":{"gte":1e999999999999999999999}}""", ["huge"] },
        { """{"n":{"lt":-1e999999999999999999999}}""", [] },
        { """{"n":{"gt":1e999999999999999999998}}""", ["huge"] },

        // Array containment compares elements exactly.
        { """{"tags":[79228162514264337593543950337]}""", [ArrayRow] },
        { """{"tags":[79228162514264337593543950336]}""", [] },
        { """{"tags":[-0, 1e-400]}""", [ArrayRow] },
        { """{"tags":[1e-29]}""", [] },
        { """{"tags":[1e-401]}""", [] },
        { """{"tags":[1e999999999999999999999]}""", [ArrayRow] },
        { """{"tags":[1e999999999999999999998]}""", [] },
    };

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task MetadataNumberFilter_ComparesExactly(string metadataFilter, string[] expected)
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);
        foreach (var (name, literal) in Rows)
        {
            await store.Workspaces.GetOrCreateAsync(name, (JsonObject)JsonNode.Parse("{\"n\":" + literal + "}")!, null, Ct);
        }

        await store.Workspaces.GetOrCreateAsync(
            ArrayRow, (JsonObject)JsonNode.Parse("""{"tags":[0, 79228162514264337593543950337, 1e-400, 1e999999999999999999999]}""")!, null, Ct);

        var filter = FilterParser.Parse("{\"metadata\":" + metadataFilter + "}", ResourceKind.Workspace);
        var page = await store.Workspaces.ListAsync(filter, new PageRequest(), Ct);

        page.Items.Select(workspace => workspace.Name).Order(StringComparer.Ordinal)
            .ShouldBe(expected.Order(StringComparer.Ordinal));
    }
}
