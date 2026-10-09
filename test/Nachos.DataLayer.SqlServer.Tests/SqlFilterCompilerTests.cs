using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Nachos.Abstractions;
using Nachos.Abstractions.Filtering;
using Nachos.DataLayer.SqlServer.Filtering;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>What <see cref="SqlFilterCompiler"/> emits, checked without a database.</summary>
public sealed class SqlFilterCompilerTests
{
    private const string Marker = "zqxj";

    /// <summary>Values that would break out of a string literal, an identifier, a comment or a LIKE pattern.</summary>
    private static readonly string[] Hostile =
    [
        $"{Marker}'; DROP TABLE dbo.Workspaces; --",
        $"{Marker}' OR '1'='1",
        $"{Marker}]; SELECT 1; --",
        $"{Marker}[0]",
        $"{Marker}/* comment */",
        $"{Marker}*/ --",
        $"{Marker}\"quoted\"",
        $"{Marker}N'unicode'",
        $"{Marker}%_[^a]\\",
        $"{Marker}\u0000\r\n\t",
        $"{Marker}😀 שלום",
        $"{Marker}$.a.b",
        $"{Marker}@p0",
    ];

    private static (string Sql, IReadOnlyList<SqlParameter> Parameters) Compile(JsonNode filter, ResourceKind kind) =>
        SqlFilterCompiler.Compile(FilterParser.Parse(filter, kind)!, kind, "t");

    private static IEnumerable<(JsonNode Filter, ResourceKind Kind)> FiltersCarrying(string value)
    {
        foreach (var kind in new[] { ResourceKind.Workspace, ResourceKind.Peer, ResourceKind.Session })
        {
            yield return (new JsonObject { ["id"] = value }, kind);
            yield return (new JsonObject { ["id"] = new JsonObject { ["ne"] = value } }, kind);
            yield return (new JsonObject { ["id"] = new JsonArray(value, "other") }, kind);
            yield return (new JsonObject { ["id"] = new JsonObject { ["contains"] = value } }, kind);
            yield return (new JsonObject { ["id"] = new JsonObject { ["icontains"] = value } }, kind);
        }

        yield return (new JsonObject { ["peer_id"] = value }, ResourceKind.Session);
        yield return (new JsonObject { ["peer_id"] = new JsonObject { ["icontains"] = value } }, ResourceKind.Session);
        yield return (new JsonObject { ["content"] = value }, ResourceKind.Message);
        yield return (new JsonObject { ["content"] = new JsonObject { ["contains"] = value } }, ResourceKind.Message);
        yield return (new JsonObject { ["session_id"] = new JsonArray(value) }, ResourceKind.Message);
        yield return (new JsonObject { ["peer_id"] = value }, ResourceKind.Message);

        foreach (var kind in new[] { ResourceKind.Workspace, ResourceKind.Peer, ResourceKind.Session, ResourceKind.Message })
        {
            // The value as a metadata key (at the root and nested) and as every kind of metadata operand.
            yield return (new JsonObject { ["metadata"] = new JsonObject { [value] = "x" } }, kind);
            yield return (new JsonObject { ["metadata"] = new JsonObject { ["a"] = new JsonObject { [value] = 1 } } }, kind);
            yield return (new JsonObject { ["metadata"] = new JsonObject { ["k"] = value } }, kind);
            yield return (new JsonObject { ["metadata"] = new JsonObject { ["k"] = new JsonObject { ["ne"] = value } } }, kind);
            yield return (new JsonObject { ["metadata"] = new JsonObject { ["k"] = new JsonObject { ["gt"] = value, ["lte"] = value } } }, kind);
            yield return (new JsonObject { ["metadata"] = new JsonObject { ["k"] = new JsonObject { ["in"] = new JsonArray(value, 1, true) } } }, kind);
            yield return (new JsonObject { ["metadata"] = new JsonObject { ["k"] = new JsonObject { ["contains"] = value } } }, kind);
            yield return (new JsonObject { ["metadata"] = new JsonObject { ["k"] = new JsonObject { ["icontains"] = value } } }, kind);
            yield return (new JsonObject { ["metadata"] = new JsonObject { ["k"] = new JsonArray(value, 2) } }, kind);
            yield return (new JsonObject { ["metadata"] = new JsonObject { [value] = null } }, kind);
            yield return (new JsonObject { ["metadata"] = new JsonObject { [value] = "*" } }, kind);
        }
    }

    [Fact]
    public void ProducesOnlyParameters()
    {
        var compiled = 0;
        foreach (var value in Hostile)
        {
            foreach (var (filter, kind) in FiltersCarrying(value))
            {
                var (sql, parameters) = Compile(filter, kind);
                compiled++;

                sql.ShouldNotContain(Marker, Case.Insensitive, $"a user value leaked into the SQL of {filter.ToJsonString()}");
                parameters.ShouldNotBeEmpty();
                parameters.ShouldAllBe(p => p.ParameterName.StartsWith("@f", StringComparison.Ordinal));
                parameters.Select(p => p.ParameterName).Distinct().Count().ShouldBe(parameters.Count);
                foreach (var parameter in parameters)
                {
                    sql.ShouldContain(parameter.ParameterName);
                }

                // The value travels in a parameter (possibly escaped, folded into a LIKE pattern or inside a JSON list).
                parameters.Any(p => (p.Value as string)?.Contains(Marker, StringComparison.Ordinal) == true)
                    .ShouldBeTrue($"the value of {filter.ToJsonString()} is not in any parameter");
            }
        }

        compiled.ShouldBeGreaterThan(400);
    }

    [Fact]
    public void ProducesOnlyParameters_ForNumbersAndTimestamps()
    {
        // Distinctive digits that would be visible if a number were inlined.
        var (sql, parameters) = Compile(
            new JsonObject
            {
                ["metadata"] = new JsonObject
                {
                    ["n"] = new JsonObject { ["gte"] = 987654321.125, ["lt"] = 1.2345678e40 },
                    ["m"] = new JsonArray(4242424242, 7),
                },
                ["token_count"] = new JsonObject { ["gt"] = 31313131 },
                ["created_at"] = new JsonObject { ["lt"] = "2026-07-23T11:22:33.4455667Z" },
            },
            ResourceKind.Message);

        foreach (var digits in new[] { "987654321", "12345678", "4242424242", "31313131", "2026", "4455667" })
        {
            sql.ShouldNotContain(digits);
        }

        parameters.ShouldNotBeEmpty();
    }

    [Fact]
    public void EscapesLikeWildcards()
    {
        var (sql, parameters) = Compile(
            new JsonObject { ["content"] = new JsonObject { ["contains"] = "50%_off[x]\\y" } }, ResourceKind.Message);

        sql.ShouldContain("LIKE");
        sql.ShouldContain("ESCAPE N'\\'");
        parameters.Select(p => p.Value).ShouldContain("%50\\%\\_off\\[x]\\\\y%");
    }

    [Fact]
    public void EscapesLikeWildcards_InIContainsAndMetadata()
    {
        var (_, icontains) = Compile(
            new JsonObject { ["id"] = new JsonObject { ["icontains"] = "a_b%" } }, ResourceKind.Peer);
        icontains.Select(p => p.Value).ShouldContain("%a\\_b\\%%");

        var (_, metadata) = Compile(
            new JsonObject { ["metadata"] = new JsonObject { ["k"] = new JsonObject { ["contains"] = "[%]" } } }, ResourceKind.Peer);
        metadata.Select(p => p.Value).ShouldContain("%\\[\\%]%");
    }

    [Fact]
    public void InList_IsOneParameter()
    {
        var names = new JsonArray([.. Enumerable.Range(0, FilterParser.MaxListItems).Select(i => (JsonNode)$"name-{i}")]);

        var (sql, parameters) = Compile(new JsonObject { ["id"] = new JsonObject { ["in"] = names } }, ResourceKind.Workspace);

        parameters.Count.ShouldBe(1);
        sql.ShouldContain("OPENJSON");
        sql.Length.ShouldBeLessThan(1000);
    }

    [Fact]
    public void MetadataInList_IsOneListParameter()
    {
        // The parser turns a metadata "in" into OR-ed equalities; the compiler regroups them into one list.
        var values = new JsonArray([.. Enumerable.Range(0, FilterParser.MaxListItems).Select(i => (JsonNode)$"v{i}")]);

        var (sql, parameters) = Compile(
            new JsonObject { ["metadata"] = new JsonObject { ["k"] = new JsonObject { ["in"] = values } } }, ResourceKind.Peer);

        parameters.Count.ShouldBeLessThan(5);
        sql.Length.ShouldBeLessThan(4000);
    }

    [Fact]
    public void TooManyDistinctValues_IsAValidationError()
    {
        // Each AND-ed key brings its own value: past SQL Server's 2,100-parameter limit the filter is rejected (422)
        // instead of failing at execution time.
        var metadata = new JsonObject();
        for (var i = 0; i < 1500; i++)
        {
            metadata[$"key{i}"] = $"value{i}";
        }

        Should.Throw<NachosValidationException>(
            () => Compile(new JsonObject { ["metadata"] = metadata }, ResourceKind.Peer))
            .Detail.ShouldBe("The filter needs more distinct values than the SQL Server provider can send in one statement.");
    }

    [Fact]
    public void NumberList_IsKeysInChunks_NotOneParameterPerElement()
    {
        var numbers = new JsonArray([.. Enumerable.Range(0, FilterParser.MaxListItems).Select(i => (JsonNode)(i * 1000003L))]);

        var (sql, parameters) = Compile(
            new JsonObject { ["metadata"] = new JsonObject { ["k"] = new JsonObject { ["in"] = numbers } } }, ResourceKind.Peer);

        parameters.Count.ShouldBeLessThan(10);
        sql.ShouldContain("CHARINDEX");
        sql.Split("dbo.JsonNumberOrderKey(").Length.ShouldBe(2, "the stored value's key is computed in exactly one place");
    }

    [Fact]
    public void RejectsAnUnsafeTableAlias()
    {
        Should.Throw<ArgumentException>(
            () => SqlFilterCompiler.Compile(new FilterNode.MatchAll(), ResourceKind.Workspace, "t; DROP TABLE x"));
    }

    [Fact]
    public void NullFilter_MatchesEverything()
    {
        var (sql, parameters) = SqlFilterCompiler.Compile(null, ResourceKind.Workspace, "t");

        sql.ShouldBe("(1 = 1)");
        parameters.ShouldBeEmpty();
    }
}
