using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Schema;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.SqlServer.Schema;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// The application's database identity in Azure has only <c>db_datareader</c>, <c>db_datawriter</c> and
/// <c>db_ddladmin</c> (infra post-provision hooks). Numeric metadata filters call the schema's order-key functions, which
/// none of those roles may execute: the schema grants EXECUTE on them to <c>public</c>, and DacFx deploys that grant,
/// automatically, to a database that lacks it.
/// </summary>
[Collection(SqlServerDockerGroup.Name)]
public sealed class SqlLeastPrivilegeTests(SqlServerFixture fixture)
{
    private const string Workspace = "least-privilege";
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>A number of over 4000 digits, whose key the long form of the function computes.</summary>
    private static readonly string Huge = "7" + new string('3', 4500);

    /// <summary>Numeric filters of every kind the provider compiles to the order-key functions, and their matches.</summary>
    private static readonly (string Filter, string[] Peers)[] Filters =
    [
        ("""{"metadata":{"n":3}}""", ["p3"]),
        ("""{"metadata":{"n":{"ne":3}}}""", ["p1", "p2", "p4", "p5", "huge"]),
        ("""{"metadata":{"n":{"gt":2,"lte":4}}}""", ["p3", "p4"]),
        ("""{"metadata":{"n":{"in":[1,2,1e400]}}}""", ["p1", "p2"]),
        ("""{"metadata":{"tags":[2,3]}}""", ["p2"]),
        ("{\"metadata\":{\"n\":" + Huge + "}}", ["huge"]),
        ("""{"metadata":{"s":"x1"}}""", ["p1"]),
    ];

    [Fact]
    public async Task NumericFilters_RunAsTheAppRoles_OnlyWithTheExecuteGrant()
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "least-privilege");
        var app = await AppStoreAsync(database);

        // The app identity writes as well as reads.
        await app.Workspaces.GetOrCreateAsync(Workspace, null, null, Ct);
        for (var i = 1; i <= 5; i++)
        {
            await app.Peers.GetOrCreateAsync(Workspace, $"p{i}", new JsonObject { ["n"] = i, ["tags"] = new JsonArray(i, i + 1), ["s"] = $"x{i}" }, null, Ct);
        }

        await app.Peers.GetOrCreateAsync(Workspace, "huge", new JsonObject { ["n"] = JsonNode.Parse(Huge) }, null, Ct);

        await AssertFiltersAsync(app);

        // Without the grant (a database deployed before it existed), the app identity cannot run a numeric filter.
        await ExecuteAsync(
            database.ConnectionString,
            "REVOKE EXECUTE ON OBJECT::[dbo].[JsonNumberOrderKey] FROM PUBLIC; REVOKE EXECUTE ON OBJECT::[dbo].[JsonNumberOrderKeyLong] FROM PUBLIC;");
        var denied = await Should.ThrowAsync<Exception>(() => NamesAsync(app, Filters[0].Filter));
        FindSql(denied).ShouldNotBeNull(denied.ToString()).Number.ShouldBe(229, denied.ToString());
        (await NamesAsync(app, """{"metadata":{"s":"x1"}}""")).ShouldBe(["p1"], "string filters do not need the functions");

        // The missing grant is an auto-safe change: an unattended upgrade restores it.
        var report = await new SchemaDeployer(database.Options).DeployAsync(DeployApproval.AutoSafeOnly, allowDataLoss: false, adoptUnstamped: false, Ct);
        report.Classification.ShouldBe(DeployClassification.AutoSafe);
        report.Applied.ShouldBeTrue();
        await AssertFiltersAsync(app);
    }

    private static async Task AssertFiltersAsync(IMemoryStore store)
    {
        foreach (var (filter, peers) in Filters)
        {
            (await NamesAsync(store, filter)).ShouldBe(peers, ignoreOrder: true, filter.Length > 80 ? filter[..80] : filter);
        }
    }

    private static async Task<List<string>> NamesAsync(IMemoryStore store, string filter)
    {
        var page = await store.Peers.ListAsync(Workspace, PeerKind.All, FilterParser.Parse(filter, ResourceKind.Peer), new PageRequest(1, 100), Ct);
        return [.. page.Items.Select(p => p.Name)];
    }

    /// <summary>A store connecting as a new login whose user has only the three roles the Azure app identity gets.</summary>
    private static async Task<SqlMemoryStore> AppStoreAsync(SqlTestDatabase database)
    {
        var login = $"app_{Guid.NewGuid():N}";
        var password = $"Aa1!{Guid.NewGuid():N}";
        await ExecuteAsync(
            database.ConnectionString,
            $"""
            CREATE LOGIN [{login}] WITH PASSWORD = N'{password}', CHECK_POLICY = OFF;
            CREATE USER [{login}] FOR LOGIN [{login}];
            ALTER ROLE [db_datareader] ADD MEMBER [{login}];
            ALTER ROLE [db_datawriter] ADD MEMBER [{login}];
            ALTER ROLE [db_ddladmin] ADD MEMBER [{login}];
            """);

        var options = new SqlServerOptions
        {
            ConnectionString = new SqlConnectionStringBuilder(database.ConnectionString) { UserID = login, Password = password }.ConnectionString,
            AutomaticSchemaDeploymentEnabled = false,
        };
        return new SqlMemoryStore(options, new SchemaGate(new SchemaDeployer(options), options), TimeProvider.System);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static SqlException? FindSql(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is SqlException sql)
            {
                return sql;
            }
        }

        return null;
    }
}
