using Nachos.Abstractions.Schema;
using Shouldly;
using static Nachos.DataLayer.SqlServer.Tests.SchemaTestSql;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>The dacpac deploys, and stays current, on a server with no Full-Text Search (spec §7.3: FTS is created at runtime, not in the dacpac).</summary>
[Collection(SqlServerNoFtsDockerGroup.Name)]
public sealed class M3SchemaNoFtsTests(SqlServerNoFtsFixture fixture)
{
    [Fact]
    public async Task DacpacDeploys_WithoutFullTextInstalled()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        (await ScalarAsync<int>(connectionString, "SELECT CAST(SERVERPROPERTY('IsFullTextInstalled') AS int)")).ShouldBe(0);

        await DeployAsync(connectionString);

        (await Deployer(connectionString).GetStatusAsync(default)).State.ShouldBe(SchemaState.Current);
        (await ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM sys.fulltext_indexes")).ShouldBe(0);
    }

    [Fact]
    public async Task M3Tables_DeployToEmptyDatabase()
    {
        var connectionString = await fixture.CreateDatabaseAsync();

        await DeployAsync(connectionString);

        (await M3TableCountAsync(connectionString)).ShouldBe(M3Tables.Length);
        (await Deployer(connectionString).ReportAsync(default)).HasPendingChanges.ShouldBeFalse();
    }
}
