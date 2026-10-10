using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.SqlServer.Schema;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// Only a filtered list's statements that SQL Server cannot compile become the 422 of <c>SqlFilterLimitTests</c>; every
/// other failure of a filtered list propagates unchanged (partner review round 2 addendum): cancellation, a command
/// timeout, a permission error and any other SQL error.
/// </summary>
[Collection(SqlServerDockerGroup.Name)]
public sealed class SqlFilterErrorPropagationTests(SqlServerFixture fixture)
{
    private const string Workspace = "propagation";
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>A filter SQL Server takes seconds to compile (3000 conditions that share nothing), but can compile.</summary>
    private static readonly FilterNode SlowFilter = FilterParser.Parse(
        "{\"OR\":[" + string.Join(",", Enumerable.Range(0, 1500).Select(i =>
            "{\"metadata\":{\"k" + i.ToString(CultureInfo.InvariantCulture) + "\":1,\"c" + i.ToString(CultureInfo.InvariantCulture) + "\":{\"contains\":\"v1\"}}}")) + "]}",
        ResourceKind.Peer)!;

    private static readonly FilterNode NumericFilter = FilterParser.Parse("""{"metadata":{"n":{"gt":1}}}""", ResourceKind.Peer)!;

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var (store, _) = await SeedAsync("propagation-cancel");

        // Cancelled before the filtered statement runs: OperationCanceledException.
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            var thrown = await CatchAsync(() => ListAsync(store, SlowFilter, cancelled.Token));
            thrown.ShouldBeAssignableTo<OperationCanceledException>(thrown.ToString());
        }

        // Cancelled while SQL Server compiles it: SqlClient's own "Operation cancelled by user" error, as it is.
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var midway = await CatchAsync(() => ListAsync(store, SlowFilter, cancel.Token));
        cancel.IsCancellationRequested.ShouldBeTrue();
        midway.ShouldNotBeOfType<NachosValidationException>();
        if (midway is not OperationCanceledException)
        {
            Sql(midway).Message.ShouldContain("Operation cancelled by user");
        }
    }

    [Fact]
    public async Task CommandTimeout_Propagates()
    {
        var (_, database) = await SeedAsync("propagation-timeout");
        var options = new SqlServerOptions
        {
            ConnectionString = new SqlConnectionStringBuilder(database.ConnectionString) { CommandTimeout = 1 }.ConnectionString,
        };
        var store = new SqlMemoryStore(options, database.Gate, TimeProvider.System);

        var thrown = await CatchAsync(() => ListAsync(store, SlowFilter, Ct));

        thrown.ShouldNotBeOfType<NachosValidationException>();
        Sql(thrown).Number.ShouldBe(-2, thrown.ToString());
    }

    [Fact]
    public async Task PermissionDenied_Propagates()
    {
        var (_, database) = await SeedAsync("propagation-permission");
        var login = $"app_{Guid.NewGuid():N}";
        var password = $"Aa1!{Guid.NewGuid():N}";
        await ExecuteAsync(
            database.ConnectionString,
            $"""
            CREATE LOGIN [{login}] WITH PASSWORD = N'{password}', CHECK_POLICY = OFF;
            CREATE USER [{login}] FOR LOGIN [{login}];
            ALTER ROLE [db_datareader] ADD MEMBER [{login}];
            REVOKE EXECUTE ON OBJECT::[dbo].[JsonNumberOrderKey] FROM PUBLIC;
            """);
        var options = new SqlServerOptions
        {
            ConnectionString = new SqlConnectionStringBuilder(database.ConnectionString) { UserID = login, Password = password }.ConnectionString,
        };
        var store = new SqlMemoryStore(options, new SchemaGate(new SchemaDeployer(options), options), TimeProvider.System);

        var thrown = await CatchAsync(() => ListAsync(store, NumericFilter, Ct));

        thrown.ShouldNotBeOfType<NachosValidationException>();
        Sql(thrown).Number.ShouldBe(229, thrown.ToString());
    }

    [Fact]
    public async Task OtherSqlErrors_Propagate()
    {
        var (store, database) = await SeedAsync("propagation-other");
        await ExecuteAsync(database.ConnectionString, "DROP FUNCTION [dbo].[JsonNumberOrderKey];");

        var thrown = await CatchAsync(() => ListAsync(store, NumericFilter, Ct));

        thrown.ShouldNotBeOfType<NachosValidationException>();
        Sql(thrown).Number.ShouldNotBeOneOf(8632, 8623, 8621, 191);
    }

    private static Task<Page<PeerRecord>> ListAsync(IMemoryStore store, FilterNode filter, CancellationToken ct) =>
        store.Peers.ListAsync(Workspace, PeerKind.All, filter, new PageRequest(1, 10), ct);

    private static async Task<Exception> CatchAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception thrown)
        {
            return thrown;
        }

        throw new ShouldAssertException("The list did not fail.");
    }

    private static SqlException Sql(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                return sql;
            }
        }

        throw new ShouldAssertException($"No SqlException in {exception}");
    }

    private async Task<(IMemoryStore Store, SqlTestDatabase Database)> SeedAsync(string purpose)
    {
        var database = await SqlTestDatabase.GetAsync(fixture, purpose);
        var store = database.CreateStore(TimeProvider.System);
        await store.Workspaces.GetOrCreateAsync(Workspace, null, null, Ct);
        for (var i = 0; i < 5; i++)
        {
            await store.Peers.GetOrCreateAsync(Workspace, $"p{i}", new JsonObject { ["n"] = i, ["c5"] = "v1" }, null, Ct);
        }

        return (store, database);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
