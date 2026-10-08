using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Nachos.Abstractions;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// Metadata and configuration live in SQL Server's native <c>json</c> columns, which store a number exactly only when
/// it is a plain decimal of at most 38 significant digits. These tests pin that domain, the provider's normalization
/// into it, and the rejection of everything outside it, so a stored value is never silently altered.
/// </summary>
[Collection(SqlServerDockerGroup.Name)]
public sealed class SqlJsonFidelityTests(SqlServerFixture fixture)
{
    private const string Max38 = "99999999999999999999999999999999999999";

    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>Input number literal and the exact text the store keeps for it.</summary>
    public static TheoryData<string, string> StorableNumbers => new()
    {
        { "0", "0" },
        { "1", "1" },
        { "1.0", "1.0" },
        { "1.50", "1.50" },
        { "-12.5", "-12.5" },
        { "0.1", "0.1" },
        { "123.000000000000", "123.000000000000" },
        { Max38, Max38 },
        { "-" + Max38, "-" + Max38 },
        { "0.12345678901234567890123456789012345678", "0.12345678901234567890123456789012345678" },
        { "0.00000000000000000000000000000000000001", "0.00000000000000000000000000000000000001" },
        { "1234567890123456789012345678901234567.1", "1234567890123456789012345678901234567.1" },
        { "79228162514264337593543950336", "79228162514264337593543950336" },
        { "79228162514264337593543950337", "79228162514264337593543950337" },

        // Exponent forms become the plain decimal of the same value (the column would round them through float).
        { "1e2", "100" },
        { "1E+2", "100" },
        { "1.0e2", "100" },
        { "2.5E-1", "0.25" },
        { "1e-29", "0.00000000000000000000000000001" },
        { "1.5e-10", "0.00000000015" },
        { "1e37", "10000000000000000000000000000000000000" },
        { "12345678901234567890123456789012345678e-10", "1234567890123456789012345678.9012345678" },
        { "-0.0e5", "0" },
        { "0e-50", "0" },

        // A plain literal longer than 38 digits is kept when only trailing fraction zeros make it long.
        { "1.0000000000000000000000000000000000000000", "1" },
    };

    /// <summary>Numbers the column cannot hold exactly: they are rejected, never stored altered.</summary>
    public static TheoryData<string> UnstorableNumbers => new()
    {
        "1e38",
        Max38 + "9",
        "-" + Max38 + "9",
        "0.000000000000000000000000000000000000001",
        "1.12345678901234567890123456789012345678",
        "1e-39",
        "1e-400",
        "1e400",
        "1.7976931348623157e308",
        "123456789012345678901234567890123456789012345678901234567890",
    };

    private async Task<SqlMemoryStore> StoreAsync() =>
        (await SqlTestDatabase.GetAsync(fixture, "json-fidelity")).CreateStore(TimeProvider.System);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [Theory]
    [MemberData(nameof(StorableNumbers))]
    public async Task Metadata_StorableNumber_IsStoredExactly(string literal, string stored)
    {
        var store = await StoreAsync();
        var name = Unique("ws");
        var metadata = new JsonObject { ["n"] = JsonNode.Parse(literal), ["nested"] = new JsonArray(JsonNode.Parse(literal)) };

        var created = await store.Workspaces.GetOrCreateAsync(name, metadata, metadata.DeepClone().AsObject(), Ct);
        var reread = await store.Workspaces.GetAsync(name, Ct);

        var expected = $$"""{"n":{{stored}},"nested":[{{stored}}]}""";
        created.Metadata.ToJsonString().ShouldBe(expected);
        created.Configuration.ToJsonString().ShouldBe(expected);
        reread!.Metadata.ToJsonString().ShouldBe(expected);
        reread.Configuration.ToJsonString().ShouldBe(expected);
    }

    [Fact]
    public async Task Metadata_ClrNumbers_AreStoredExactly()
    {
        var store = await StoreAsync();
        var name = Unique("ws");
        var metadata = new JsonObject
        {
            ["double"] = 0.1,
            ["small"] = 1e-7,
            ["float"] = 1.5f,
            ["decimal"] = 79228162514264337593543950335m,
            ["ulong"] = ulong.MaxValue,
            ["long"] = long.MinValue,
        };

        var created = await store.Workspaces.GetOrCreateAsync(name, metadata, null, Ct);

        const string expected = """{"double":0.1,"small":0.0000001,"float":1.5,"decimal":79228162514264337593543950335,"ulong":18446744073709551615,"long":-9223372036854775808}""";
        created.Metadata.ToJsonString().ShouldBe(expected);
        (await store.Workspaces.GetAsync(name, Ct))!.Metadata.ToJsonString().ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(UnstorableNumbers))]
    public async Task Metadata_UnstorableNumber_IsRejected_AndNothingChanges(string literal)
    {
        var store = await StoreAsync();
        var name = Unique("ws");
        JsonObject Payload() => new() { ["deep"] = new JsonObject { ["n"] = new JsonArray(JsonNode.Parse(literal)) } };

        await Should.ThrowAsync<NachosValidationException>(() => store.Workspaces.GetOrCreateAsync(name, Payload(), null, Ct));
        (await store.Workspaces.GetAsync(name, Ct)).ShouldBeNull();

        await store.Workspaces.GetOrCreateAsync(name, new JsonObject { ["keep"] = 1 }, null, Ct);
        await Should.ThrowAsync<NachosValidationException>(() => store.Workspaces.UpdateAsync(name, null, Payload(), Ct));
        await store.Sessions.GetOrCreateAsync(name, "s", null, null, null, Ct);
        await Should.ThrowAsync<NachosValidationException>(
            () => store.Messages.AppendAsync(name, "s", [new("alice", "hi", 1, Payload(), null)], null, Ct));

        var workspace = await store.Workspaces.GetAsync(name, Ct);
        workspace!.Metadata.ToJsonString().ShouldBe("""{"keep":1}""");
        workspace.Configuration.ToJsonString().ShouldBe("{}");
        (await store.Messages.ListAsync(name, "s", null, new PageRequest(), Ct)).Total.ShouldBe(0);
        (await store.Peers.GetAsync(name, "alice", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Metadata_KeyOfMoreThan4000Units_IsRejected_AndOf4000IsFilterable()
    {
        var store = await StoreAsync();
        var workspace = Unique("ws");
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        var longest = new string('k', 4000);

        await Should.ThrowAsync<NachosValidationException>(
            () => store.Peers.GetOrCreateAsync(workspace, "p", new JsonObject { [longest + "k"] = 1 }, null, Ct));
        await store.Peers.GetOrCreateAsync(workspace, "p", new JsonObject { [longest] = 1 }, null, Ct);

        var filter = Abstractions.Filtering.FilterParser.Parse(
            new JsonObject { ["metadata"] = new JsonObject { [longest] = 1 } }, Abstractions.Filtering.ResourceKind.Peer);
        (await store.Peers.ListAsync(workspace, Abstractions.Domain.PeerKind.All, filter, new PageRequest(), Ct)).Total.ShouldBe(1);

        // A filter key the store can never hold matches like any missing key.
        var longer = Abstractions.Filtering.FilterParser.Parse(
            new JsonObject { ["metadata"] = new JsonObject { [longest + "k"] = new JsonObject { ["ne"] = 1 } } },
            Abstractions.Filtering.ResourceKind.Peer);
        (await store.Peers.ListAsync(workspace, Abstractions.Domain.PeerKind.All, longer, new PageRequest(), Ct)).Total.ShouldBe(1);
    }

    /// <summary>
    /// Evidence for the rule above: what the column itself does to the literals the store rewrites or rejects. If a
    /// future SQL Server stores these exactly, this test fails and the ingress rule can be relaxed.
    /// </summary>
    [Theory]
    [InlineData("1e2", "100.0000000000")]
    [InlineData("1e-29", "0.0000000000")]
    [InlineData("1.5e-10", "0.0000000001")]
    [InlineData("12345678901234567890123456789012345678e-10", "1234567890123456850245451776.0000000000")]
    [InlineData("1.12345678901234567890123456789012345678", "1.1234567890")]
    [InlineData("0.000000000000000000000000000000000000001", "0.0000000000")]
    [InlineData("-0", "0")]
    [InlineData("1e38", null)]
    [InlineData("1e37", null)]
    [InlineData(Max38 + "9", null)]
    public async Task JsonColumn_AltersOrRejectsTheseLiterals(string literal, string? columnText)
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "json-fidelity");
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand(
            "DECLARE @j json = @text; SELECT [value] FROM OPENJSON(@j) WHERE [key] = N'n';", connection);
        command.Parameters.Add("@text", System.Data.SqlDbType.NVarChar, -1).Value = $$"""{"n":{{literal}}}""";

        if (columnText is null)
        {
            (await Should.ThrowAsync<SqlException>(() => command.ExecuteScalarAsync(Ct))).Number.ShouldBe(1007);
        }
        else
        {
            (await command.ExecuteScalarAsync(Ct)).ShouldBe(columnText);
        }
    }
}
