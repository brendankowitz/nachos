using Nachos.Abstractions.Schema;
using Nachos.DataLayer.SqlServer.Schema;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>Argument checks that fire before the deployer touches a server, so they need no database.</summary>
public sealed class SchemaDeployerGuardTests
{
    private static SchemaDeployer Deployer(string connectionString) => new(new SqlServerOptions { ConnectionString = connectionString });

    [Theory]
    [InlineData("Server=localhost")]
    [InlineData("Server=localhost;Initial Catalog=")]
    [InlineData("Server=localhost;Initial Catalog=master")]
    [InlineData("Server=localhost;Database=MODEL")]
    [InlineData("Server=localhost;Initial Catalog=msdb")]
    [InlineData("Server=localhost;Initial Catalog=tempdb")]
    public async Task ConnectionStringWithoutAUserDatabase_IsRefused(string connectionString)
    {
        var deployer = Deployer(connectionString);

        await Should.ThrowAsync<InvalidOperationException>(() => deployer.GetStatusAsync(default));
        await Should.ThrowAsync<InvalidOperationException>(() => deployer.ReportAsync(default));
        await Should.ThrowAsync<InvalidOperationException>(() => deployer.DeployAsync(DeployApproval.OperatorReviewed, true, true, default));
    }

    [Fact]
    public async Task AllowDataLossWithoutOperatorReview_IsRefused()
    {
        var deployer = Deployer("Server=localhost;Initial Catalog=nachos");

        await Should.ThrowAsync<ArgumentException>(() => deployer.DeployAsync(DeployApproval.AutoSafeOnly, allowDataLoss: true, adoptUnstamped: false, default));
    }
}