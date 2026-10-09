using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// Metadata and configuration are stored losslessly as text in <c>nvarchar(max)</c> columns that a CHECK constraint
/// requires to hold a JSON object: the canonical text of the strict-JSON-data helper round-trips byte for byte, number
/// spelling included, with no normalization and no size cap.
/// </summary>
[Collection(SqlServerDockerGroup.Name)]
public sealed class SqlJsonFidelityTests(SqlServerFixture fixture)
{
    private const string TwoPow96 = "79228162514264337593543950336";

    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>Number literals that must come back exactly as written.</summary>
    public static TheoryData<string> Numbers => new()
    {
        "0", "-0", "0.0", "-0.0", "0e5", "1", "1.0", "1.50", "-12.5", "0.1",
        "1e2", "1E2", "1E+2", "1e-2", "1.50e1", "2.5E-1", "1e-29", "1E400", "-1e-400", "5E-324", "1.7976931348623157E+308",
        TwoPow96, "79228162514264337593543950335", "79228162514264337593543950337", "-79228162514264337593543950337",
        "1234567890123456789012345678901234567890", "0.12345678901234567890123456789012345678901234567890",
        "1e999999999999999999999", "-1.5e-999999999999999999999", new string('9', 5000), "0." + new string('0', 4000) + "1",
    };

    private async Task<SqlMemoryStore> StoreAsync() =>
        (await SqlTestDatabase.GetAsync(fixture, "json-fidelity")).CreateStore(TimeProvider.System);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [Theory]
    [MemberData(nameof(Numbers))]
    public async Task Number_RoundTripsByteForByte(string literal)
    {
        var store = await StoreAsync();
        var name = Unique("ws");
        var metadata = new JsonObject { ["n"] = JsonNode.Parse(literal), ["nested"] = new JsonArray(JsonNode.Parse(literal)) };
        var expected = $$"""{"n":{{literal}},"nested":[{{literal}}]}""";

        var created = await store.Workspaces.GetOrCreateAsync(name, metadata, metadata.DeepClone().AsObject(), Ct);

        created.Metadata.ToJsonString().ShouldBe(expected);
        created.Configuration.ToJsonString().ShouldBe(expected);
        var reread = await store.Workspaces.GetAsync(name, Ct);
        reread!.Metadata.ToJsonString().ShouldBe(expected);
        reread.Configuration.ToJsonString().ShouldBe(expected);
        (await RawColumnAsync(name)).ShouldBe(expected, "the column holds exactly the helper's canonical text");

        await store.Sessions.GetOrCreateAsync(name, "s", null, null, null, Ct);
        var message = (await store.Messages.AppendAsync(name, "s", [new("alice", "hi", 1, metadata.DeepClone().AsObject(), null)], null, Ct))[0];
        message.Metadata.ToJsonString().ShouldBe(expected);
        (await store.Messages.GetAsync(name, "s", message.PublicId, Ct))!.Metadata.ToJsonString().ShouldBe(expected);
    }

    [Fact]
    public async Task Metadata_ClrNumbers_AreStoredAsTheHelperWritesThem()
    {
        var store = await StoreAsync();
        var name = Unique("ws");
        var metadata = new JsonObject
        {
            ["double"] = 0.1,
            ["small"] = 1e-7,
            ["big"] = 1e300,
            ["tiniest"] = double.Epsilon,
            ["float"] = 1.5f,
            ["decimal"] = 79228162514264337593543950335m,
            ["ulong"] = ulong.MaxValue,
            ["long"] = long.MinValue,
        };
        var expected = Abstractions.Json.StrictJsonData.ToCanonical(metadata)!.ToJsonString();

        var created = await store.Workspaces.GetOrCreateAsync(name, metadata, null, Ct);

        created.Metadata.ToJsonString().ShouldBe(expected);
        (await RawColumnAsync(name)).ShouldBe(expected);
    }

    [Fact]
    public async Task Number_AnySpelling_IsFoundByAnExactFilter()
    {
        var store = await StoreAsync();
        var workspace = Unique("ws");
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        string[] values = ["1e2", "1E400", "-1e-400", "5E-324", TwoPow96, "79228162514264337593543950337", "-0", "1e999999999999999999999"];
        for (var i = 0; i < values.Length; i++)
        {
            await store.Peers.GetOrCreateAsync(workspace, $"p{i}", new JsonObject { ["n"] = JsonNode.Parse(values[i]) }, null, Ct);
        }

        async Task<string[]> Matching(string condition) =>
        [
            .. (await store.Peers.ListAsync(
                    workspace,
                    PeerKind.All,
                    FilterParser.Parse("{\"metadata\":{\"n\":" + condition + "}}", ResourceKind.Peer),
                    new PageRequest(),
                    Ct))
                .Items.Select(p => p.Name),
        ];

        (await Matching("100")).ShouldBe(["p0"]);
        (await Matching("100.000")).ShouldBe(["p0"]);
        (await Matching("1" + new string('0', 400))).ShouldBe(["p1"]);
        (await Matching("-0.1e-399")).ShouldBe(["p2"]);
        (await Matching("0.5e-323")).ShouldBe(["p3"]);
        (await Matching("7.9228162514264337593543950336e28")).ShouldBe(["p4"]);
        (await Matching("0")).ShouldBe(["p6"]);
        (await Matching("10e999999999999999999998")).ShouldBe(["p7"]);
        (await Matching("""{"gt":79228162514264337593543950336}""")).ShouldBe(["p1", "p5", "p7"], ignoreOrder: true);
        (await Matching("""{"gt":0,"lt":1e-300}""")).ShouldBe(["p3"]);
        (await Matching("""{"lt":0}""")).ShouldBe(["p2"]);
        (await Matching("""{"gt":1E401}""")).ShouldBe(["p7"]);
    }

    [Fact]
    public async Task Metadata_NonAsciiText_IsStoredUnescaped_AndRoundTrips()
    {
        var store = await StoreAsync();
        var name = Unique("ws");
        var metadata = new JsonObject
        {
            ["text"] = "😀𝔘 שלום مرحبا é <tag> & 'quote'",
            ["escaped"] = "quote \" backslash \\ newline \n tab \t nul \u0000",
            ["日本語"] = "キー",
        };

        var created = await store.Workspaces.GetOrCreateAsync(name, metadata, null, Ct);

        created.Metadata.ToJsonString().ShouldBe(metadata.ToJsonString());
        (await store.Workspaces.GetAsync(name, Ct))!.Metadata.ToJsonString().ShouldBe(metadata.ToJsonString());
        JsonNode.DeepEquals(created.Metadata, metadata).ShouldBeTrue();

        // The column holds BMP characters themselves, not \uXXXX escapes (supplementary ones such as emoji are always
        // escaped by the framework's encoders), and JSON's own escapes remain.
        var raw = await RawColumnAsync(name);
        raw.ShouldContain("שלום مرحبا é <tag> & 'quote'");
        raw.ShouldContain("\"日本語\":\"キー\"");
        raw.ShouldContain("\\uD83D\\uDE00", Case.Insensitive);
        raw.ShouldContain("quote \\\" backslash \\\\ newline \\n tab \\t nul \\u0000");
        raw.ShouldNotContain("\\u05E9", Case.Insensitive);

        // Filters read the stored text as the same values.
        var filter = FilterParser.Parse(new JsonObject { ["metadata"] = new JsonObject { ["日本語"] = "キー" } }, ResourceKind.Workspace);
        (await store.Workspaces.ListAsync(filter, new PageRequest(1, 100), Ct)).Items.ShouldContain(w => w.Name == name);
    }

    [Fact]
    public async Task Metadata_KeyOfMoreThan4000Units_IsRejected_AndOf4000IsFilterable()
    {
        var store = await StoreAsync();
        var workspace = Unique("ws");
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        var longest = new string('k', 4000);

        var rejected = await Should.ThrowAsync<NachosValidationException>(
            () => store.Peers.GetOrCreateAsync(workspace, "p", new JsonObject { ["a"] = new JsonObject { [longest + "k"] = 1 } }, null, Ct));
        rejected.Detail.ShouldBe("metadata has an object key longer than 4000 UTF-16 code units, which the SQL Server provider cannot store.");
        (await store.Peers.GetAsync(workspace, "p", Ct)).ShouldBeNull();

        // Configuration is never read by filters, so OPENJSON's limit does not apply to it.
        var configuration = new JsonObject { [longest + "k"] = 1 };
        var configured = await store.Peers.GetOrCreateAsync(workspace, "configured", null, configuration, Ct);
        configured.Configuration.ToJsonString().ShouldBe(configuration.ToJsonString());
        (await store.Peers.GetAsync(workspace, "configured", Ct))!.Configuration.ToJsonString().ShouldBe(configuration.ToJsonString());

        await store.Peers.GetOrCreateAsync(workspace, "p", new JsonObject { [longest] = 1 }, null, Ct);
        var filter = FilterParser.Parse(new JsonObject { ["metadata"] = new JsonObject { [longest] = 1 } }, ResourceKind.Peer);
        (await store.Peers.ListAsync(workspace, PeerKind.All, filter, new PageRequest(), Ct)).Total.ShouldBe(1);

        // A filter key the store can never hold matches like any missing key: both peers lack it.
        var longer = FilterParser.Parse(
            new JsonObject { ["metadata"] = new JsonObject { [longest + "k"] = new JsonObject { ["ne"] = 1 } } }, ResourceKind.Peer);
        (await store.Peers.ListAsync(workspace, PeerKind.All, longer, new PageRequest(), Ct)).Total.ShouldBe(2);
    }

    [Fact]
    public async Task Metadata_NestedToTheHelpersDepthLimit_RoundTripsAndFilters()
    {
        var store = await StoreAsync();
        var workspace = Unique("ws");
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);

        // 64 nested objects (the helper's limit), the innermost holding k = 1.
        JsonObject deepest = new() { ["k"] = 1 };
        var root = deepest;
        for (var level = 1; level < 64; level++)
        {
            root = new JsonObject { ["a"] = root };
        }

        var expected = root.ToJsonString();
        var created = await store.Peers.GetOrCreateAsync(workspace, "p", root, null, Ct);
        created.Metadata.ToJsonString().ShouldBe(expected);

        // The deepest path a filter can name within the parser's own 64-level limit: 63 keys, here "a" exists.
        JsonNode condition = "*";
        for (var level = 0; level < 63; level++)
        {
            condition = new JsonObject { ["a"] = condition };
        }

        var filter = FilterParser.Parse(new JsonObject { ["metadata"] = condition }, ResourceKind.Peer);
        (await store.Peers.ListAsync(workspace, PeerKind.All, filter, new PageRequest(), Ct)).Total.ShouldBe(1);
    }

    /// <summary>Every JSON column is <c>nvarchar(max)</c> with a CHECK that admits only a JSON object.</summary>
    public static TheoryData<string, string> JsonColumns => new()
    {
        { "Workspaces", "Metadata" }, { "Workspaces", "InternalMetadata" }, { "Workspaces", "Configuration" },
        { "Peers", "Metadata" }, { "Peers", "InternalMetadata" }, { "Peers", "Configuration" },
        { "Sessions", "Metadata" }, { "Sessions", "InternalMetadata" }, { "Sessions", "Configuration" },
        { "SessionPeers", "Configuration" },
        { "Messages", "Metadata" }, { "Messages", "InternalMetadata" },
    };

    [Theory]
    [MemberData(nameof(JsonColumns))]
    public async Task JsonColumn_IsNvarcharMax_WithAnObjectCheck(string table, string column)
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "json-fidelity");
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand(
            """
            SELECT TYPE_NAME(c.user_type_id), c.max_length, cc.definition, dc.definition
            FROM sys.columns AS c
            LEFT JOIN sys.check_constraints AS cc ON cc.parent_object_id = c.object_id AND cc.name = @check
            LEFT JOIN sys.default_constraints AS dc ON dc.object_id = c.default_object_id
            WHERE c.object_id = OBJECT_ID(@table) AND c.name = @column
            """,
            connection);
        command.Parameters.AddWithValue("@table", $"dbo.{table}");
        command.Parameters.AddWithValue("@column", column);
        command.Parameters.AddWithValue("@check", $"CK_{table}_{column}_IsJsonObject");
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();

        reader.GetString(0).ShouldBe("nvarchar");
        reader.GetInt16(1).ShouldBe((short)-1);
        reader.GetString(2).ShouldBe($"(isjson([{column}],OBJECT)=(1))");
        reader.GetString(3).ShouldBe("(N'{}')");
    }

    [Theory]
    [InlineData("""[1,2]""")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{\"unterminated\":")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task JsonColumn_RejectsNonObjectText_WrittenDirectly(string text)
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "json-fidelity");
        var store = database.CreateStore(TimeProvider.System);
        var name = Unique("ws");
        await store.Workspaces.GetOrCreateAsync(name, null, null, Ct);
        await store.Sessions.GetOrCreateAsync(name, "s", null, null, null, Ct);
        await store.Messages.AppendAsync(name, "s", [new("alice", "hi", 1, null, null)], null, Ct);

        string[] updates =
        [
            "UPDATE dbo.Workspaces SET Metadata = @text WHERE Name = @name",
            "UPDATE dbo.Workspaces SET Configuration = @text WHERE Name = @name",
            "UPDATE p SET Metadata = @text FROM dbo.Peers p JOIN dbo.Workspaces w ON w.Id = p.WorkspaceId WHERE w.Name = @name",
            "UPDATE s SET Configuration = @text FROM dbo.Sessions s JOIN dbo.Workspaces w ON w.Id = s.WorkspaceId WHERE w.Name = @name",
            "UPDATE sp SET Configuration = @text FROM dbo.SessionPeers sp JOIN dbo.Workspaces w ON w.Id = sp.WorkspaceId WHERE w.Name = @name",
            "UPDATE m SET Metadata = @text FROM dbo.Messages m JOIN dbo.Workspaces w ON w.Id = m.WorkspaceId WHERE w.Name = @name",
            "UPDATE m SET InternalMetadata = @text FROM dbo.Messages m JOIN dbo.Workspaces w ON w.Id = m.WorkspaceId WHERE w.Name = @name",
        ];

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        foreach (var update in updates)
        {
            await using var command = new SqlCommand(update, connection);
            command.Parameters.Add("@text", System.Data.SqlDbType.NVarChar, -1).Value = text;
            command.Parameters.AddWithValue("@name", name);

            // 547: the statement conflicted with a CHECK constraint.
            (await Should.ThrowAsync<SqlException>(() => command.ExecuteNonQueryAsync(Ct), update)).Number.ShouldBe(547);
        }

        // An object written directly is accepted, whitespace and all.
        await using var accepted = new SqlCommand("UPDATE dbo.Workspaces SET Metadata = N' { \"a\" : 1 } ' WHERE Name = @name", connection);
        accepted.Parameters.AddWithValue("@name", name);
        (await accepted.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
    }

    private async Task<string> RawColumnAsync(string workspace)
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "json-fidelity");
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand("SELECT Metadata FROM dbo.Workspaces WHERE Name = @name", connection);
        command.Parameters.AddWithValue("@name", workspace);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }
}
