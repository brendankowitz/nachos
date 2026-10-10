using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Schema;
using Nachos.DataLayer.SqlServer.Schema;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// A pre-release database deployed before the Task 7 partner-review changes (schema version 1 at <c>49c2b03</c>) upgrades
/// to the current version-1 schema exactly as the spec (§7.3) says: the gate leaves it alone, an unattended upgrade is
/// refused, and an operator-reviewed upgrade applies the function grants and the binary <c>Role</c> collation without
/// changing a row.
/// </summary>
/// <remarks>
/// The old shape is made by script from a current deploy rather than by deploying a dacpac built from the
/// <c>49c2b03</c> sources: only the changed objects differ (no EXECUTE grants on the two order-key functions;
/// <c>PrincipalGrants.Role</c> in the database's default collation <c>SQL_Latin1_General_CP1_CI_AS</c>, with the unique
/// constraint rebuilt over it), and the script avoids checking a second schema into the test fixtures. A DeployReport of a
/// dacpac built from <c>49c2b03</c> against the scripted database lists no operations (checked when this test was written).
/// </remarks>
[Collection(SqlServerDockerGroup.Name)]
public sealed class SqlSchemaTransitionTests(SqlServerFixture fixture)
{
    private const string OldCollation = "SQL_Latin1_General_CP1_CI_AS";
    private const string NewCollation = "Latin1_General_100_BIN2_UTF8";
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task DatabaseFrom49c2b03_NeedsOneReviewedUpgrade_AndKeepsEveryRow()
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "schema-transition");
        var store = database.CreateStore(TimeProvider.System);
        await store.Workspaces.GetOrCreateAsync("w", null, null, Ct);
        await store.Peers.GetOrCreateAsync("w", "p1", new JsonObject { ["n"] = 1 }, null, Ct);
        await store.Peers.GetOrCreateAsync("w", "p2", new JsonObject { ["n"] = 2 }, null, Ct);

        // The 49c2b03 shape of the changed objects, and grants that are distinct under its case-insensitive Role (case
        // differences in ObjectId, which was already binary; trailing spaces in either column).
        await ExecuteAsync(
            database.ConnectionString,
            $"""
            REVOKE EXECUTE ON OBJECT::[dbo].[JsonNumberOrderKey] FROM PUBLIC;
            REVOKE EXECUTE ON OBJECT::[dbo].[JsonNumberOrderKeyLong] FROM PUBLIC;
            ALTER TABLE [dbo].[PrincipalGrants] DROP CONSTRAINT [UQ_PrincipalGrants_Object_Workspace_Role];
            ALTER TABLE [dbo].[PrincipalGrants] ALTER COLUMN [Role] NVARCHAR (32) COLLATE {OldCollation} NOT NULL;
            ALTER TABLE [dbo].[PrincipalGrants] ADD CONSTRAINT [UQ_PrincipalGrants_Object_Workspace_Role] UNIQUE NONCLUSTERED ([ObjectId], [WorkspaceId], [Role]);
            DECLARE @w BIGINT = (SELECT [Id] FROM [dbo].[Workspaces] WHERE [Name] = N'w');
            INSERT [dbo].[PrincipalGrants] ([ObjectId], [WorkspaceId], [Role]) VALUES
                (N'obj-a', NULL, N'Nachos.Admin'),
                (N'obj-a', NULL, N'Nachos.Workspace'),
                (N'OBJ-A', NULL, N'NACHOS.ADMIN'),
                (N'obj-b', @w, N'nachos.workspace '),
                (N'obj-b ', NULL, N'Nachos.Admin  '),
                (N'obj-c', @w, N'Nachos.Workspace');
            """);
        var rows = await GrantRowsAsync(database.ConnectionString);
        rows.Count.ShouldBe(6);
        (await RoleCollationAsync(database.ConnectionString)).ShouldBe(OldCollation);
        (await PublicExecuteGrantsAsync(database.ConnectionString)).ShouldBe(0);

        // Stamped version 1, so the gate takes it as current and changes nothing.
        var automatic = new SqlServerOptions { ConnectionString = database.ConnectionString, AutomaticSchemaDeploymentEnabled = true };
        await new SchemaGate(new SchemaDeployer(automatic), automatic).EnsureAsync(Ct);
        (await RoleCollationAsync(database.ConnectionString)).ShouldBe(OldCollation);
        (await PublicExecuteGrantsAsync(database.ConnectionString)).ShouldBe(0);

        // An unattended upgrade refuses: the collation change rebuilds the unique constraint and alters the column.
        var deployer = new SchemaDeployer(database.Options);
        var refused = await Should.ThrowAsync<SchemaDeployRefusedException>(
            () => deployer.DeployAsync(DeployApproval.AutoSafeOnly, allowDataLoss: false, adoptUnstamped: false, Ct));
        refused.Reason.ShouldBe(SchemaRefusalReason.NotAutoSafe);
        refused.PossibleDataLoss.ShouldBeFalse();
        (await RoleCollationAsync(database.ConnectionString)).ShouldBe(OldCollation);

        // A reviewed upgrade applies both changes and loses nothing.
        var report = await deployer.DeployAsync(DeployApproval.OperatorReviewed, allowDataLoss: false, adoptUnstamped: false, Ct);
        report.Applied.ShouldBeTrue();
        (await GrantRowsAsync(database.ConnectionString)).ShouldBe(rows);
        (await RoleCollationAsync(database.ConnectionString)).ShouldBe(NewCollation);
        (await PublicExecuteGrantsAsync(database.ConnectionString)).ShouldBe(2);
        (await deployer.ReportAsync(Ct)).HasPendingChanges.ShouldBeFalse();

        // The app identity's roles can now run numeric filters.
        var app = await AppStoreAsync(database);
        var page = await app.Peers.ListAsync("w", PeerKind.All, FilterParser.Parse("""{"metadata":{"n":{"gt":1}}}""", ResourceKind.Peer), new PageRequest(1, 10), Ct);
        page.Items.Select(peer => peer.Name).ShouldBe(["p2"]);
    }

    /// <summary>Every grant row, byte for byte: id, hex of the object id and role code units, and the workspace id.</summary>
    private static async Task<List<string>> GrantRowsAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand(
            "SELECT CONCAT([Id], '|', CONVERT(varchar(max), CAST([ObjectId] AS varbinary(max)), 2), '|', [WorkspaceId], '|', CONVERT(varchar(max), CAST([Role] AS varbinary(max)), 2)) FROM [dbo].[PrincipalGrants] ORDER BY [Id]",
            connection);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private static async Task<string> RoleCollationAsync(string connectionString) =>
        (string)(await ScalarAsync(connectionString, "SELECT [collation_name] FROM sys.columns WHERE [object_id] = OBJECT_ID(N'dbo.PrincipalGrants') AND [name] = N'Role'"))!;

    private static async Task<int> PublicExecuteGrantsAsync(string connectionString) =>
        (int)(await ScalarAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM sys.database_permissions
            WHERE [grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'public') AND [permission_name] = N'EXECUTE' AND [state] = 'G'
              AND [major_id] IN (OBJECT_ID(N'dbo.JsonNumberOrderKey'), OBJECT_ID(N'dbo.JsonNumberOrderKeyLong'))
            """))!;

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
        };
        return new SqlMemoryStore(options, new SchemaGate(new SchemaDeployer(options), options), TimeProvider.System);
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(Ct);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
