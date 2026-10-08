using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using Nachos.Abstractions.Schema;
using Nachos.DataLayer.SqlServer.Schema;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// Runs the real deployer against SQL Server 2025 in Docker (see <see cref="SqlServerFixture"/>). Change scenarios are built by
/// deploying the schema, then altering the <em>database</em> backwards (dropping a column, say) and lowering the stamped version,
/// so the current dacpac's diff contains the change under test.
/// </summary>
[Collection(SqlServerDockerGroup.Name)]
public sealed partial class SchemaDeployerTests(SqlServerFixture fixture)
{
    private static SchemaDeployer Deployer(string connectionString) => new(new SqlServerOptions { ConnectionString = connectionString });

    private static Task<SchemaReport> AutoSafeDeployAsync(string connectionString) =>
        Deployer(connectionString).DeployAsync(DeployApproval.AutoSafeOnly, allowDataLoss: false, adoptUnstamped: false, default);

    private static SchemaGate Gate(string connectionString, bool automatic)
    {
        var options = new SqlServerOptions { ConnectionString = connectionString, AutomaticSchemaDeploymentEnabled = automatic };
        return new SchemaGate(new SchemaDeployer(options), options);
    }

    private static async Task<T?> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> DeployedDatabaseAsync()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        (await AutoSafeDeployAsync(connectionString)).Applied.ShouldBeTrue();
        return connectionString;
    }

    private static Task<int> ColumnCountAsync(string connectionString, string table, string column) =>
        ScalarAsync<int>(connectionString, $"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'{table}') AND name = N'{column}'");

    private static Task<int> StampedVersionAsync(string connectionString) =>
        ScalarAsync<int>(connectionString, "SELECT [Version] FROM dbo.SchemaVersion");

    private static Task SetVersionAsync(string connectionString, int version) =>
        ExecuteAsync(connectionString, $"UPDATE dbo.SchemaVersion SET [Version] = {version}");

    private static int OperationCount(SchemaReport report) =>
        XDocument.Parse(report.ReportXml).Descendants().Count(e => e.Name.LocalName == "Operation");

    // ---- bootstrap, no-op, platform ----

    [Fact]
    public async Task EmptyDatabase_DeploysAndStampsVersion()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var deployer = Deployer(connectionString);
        (await deployer.GetStatusAsync(default)).State.ShouldBe(SchemaState.Empty);

        var report = await AutoSafeDeployAsync(connectionString);

        report.Applied.ShouldBeTrue();
        var status = await deployer.GetStatusAsync(default);
        status.State.ShouldBe(SchemaState.Current);
        status.Deployed.ShouldBe(SchemaInfo.CurrentVersion);
        (await ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM sys.tables WHERE name = N'Messages'")).ShouldBe(1);
    }

    [Fact]
    public async Task Deploy_SetsReadCommittedSnapshotOn()
    {
        var connectionString = await DeployedDatabaseAsync();

        (await ScalarAsync<bool>(connectionString, "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()"))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task PlatformSelection_Box2025_UsesSql2025Dacpac()
    {
        var connectionString = await fixture.CreateDatabaseAsync();

        (await Deployer(connectionString).GetStatusAsync(default)).Platform.ShouldBe("Sql2025");
    }

    [Fact]
    public async Task CurrentDatabase_IsNoOp()
    {
        var connectionString = await DeployedDatabaseAsync();
        var stampedAt = await ScalarAsync<DateTimeOffset>(connectionString, "SELECT AppliedAt FROM dbo.SchemaVersion");

        await Gate(connectionString, automatic: true).EnsureAsync(default);
        var report = await AutoSafeDeployAsync(connectionString);

        report.Applied.ShouldBeFalse();
        report.HasPendingChanges.ShouldBeFalse();
        (await ScalarAsync<DateTimeOffset>(connectionString, "SELECT AppliedAt FROM dbo.SchemaVersion")).ShouldBe(stampedAt);
    }

    [Fact]
    public async Task RedeployCurrentModel_ReportHasNoOperations()
    {
        var connectionString = await DeployedDatabaseAsync();

        var report = await Deployer(connectionString).ReportAsync(default);

        // Any operation here is drift: DacFx sees a difference between the model and what it just deployed
        // (for example a constraint written in a form SQL Server normalizes differently).
        OperationCount(report).ShouldBe(0);
        report.HasPendingChanges.ShouldBeFalse();
        report.Classification.ShouldBe(DeployClassification.AutoSafe);
    }

    [Fact]
    public async Task GateEnabled_Empty_Bootstraps()
    {
        var connectionString = await fixture.CreateDatabaseAsync();

        await Gate(connectionString, automatic: true).EnsureAsync(default);

        (await Deployer(connectionString).GetStatusAsync(default)).State.ShouldBe(SchemaState.Current);
    }

    [Fact]
    public async Task GateDisabled_Empty_ThrowsWithRemedy()
    {
        var connectionString = await fixture.CreateDatabaseAsync();

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => Gate(connectionString, automatic: false).EnsureAsync(default));

        failure.Message.ShouldContain("nachos schema upgrade");
        (await Deployer(connectionString).GetStatusAsync(default)).State.ShouldBe(SchemaState.Empty);
    }

    [Fact]
    public async Task ConcurrentDeployers_AreSerialized()
    {
        var connectionString = await fixture.CreateDatabaseAsync();

        var reports = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => AutoSafeDeployAsync(connectionString)));

        // The lock makes the others wait, then find the database current: exactly one bootstrap, no error.
        reports.Count(r => r.Applied).ShouldBe(1);
        (await Deployer(connectionString).GetStatusAsync(default)).State.ShouldBe(SchemaState.Current);
        (await ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM dbo.SchemaVersion")).ShouldBe(1);
    }

    // ---- refusals: Ahead, Unstamped (C1) ----

    [Fact]
    public async Task NonEmptyUnstampedDatabase_NeverAutoDeployed()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await ExecuteAsync(connectionString, "CREATE TABLE dbo.SomeoneElses (Id int NOT NULL)");
        var gate = Gate(connectionString, automatic: true);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => gate.EnsureAsync(default));

        failure.Message.ShouldContain("nachos schema upgrade");
        (await Deployer(connectionString).GetStatusAsync(default)).State.ShouldBe(SchemaState.Unstamped);
        (await ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM sys.tables WHERE name = N'Workspaces'")).ShouldBe(0);
    }

    [Fact]
    public async Task AheadDatabase_NeverDowngraded()
    {
        var connectionString = await DeployedDatabaseAsync();
        await SetVersionAsync(connectionString, SchemaInfo.CurrentVersion + 1);
        var gate = Gate(connectionString, automatic: true);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => gate.EnsureAsync(default));

        failure.Message.ShouldContain("nachos schema upgrade");
        var status = await Deployer(connectionString).GetStatusAsync(default);
        status.State.ShouldBe(SchemaState.Ahead);
        status.Deployed.ShouldBe(SchemaInfo.CurrentVersion + 1);
    }

    [Theory]
    [InlineData(DeployApproval.AutoSafeOnly)]
    [InlineData(DeployApproval.OperatorReviewed)]
    public async Task DeployAsync_AheadDatabase_Refused_NoChange(DeployApproval approval)
    {
        var connectionString = await DeployedDatabaseAsync();
        // A deploy would both add the missing column and lower the version, so either would show.
        await ExecuteAsync(connectionString, "ALTER TABLE dbo.SessionPeers DROP COLUMN LeftAt");
        await SetVersionAsync(connectionString, SchemaInfo.CurrentVersion + 1);

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            () => Deployer(connectionString).DeployAsync(approval, allowDataLoss: false, adoptUnstamped: true, default));

        failure.Message.ShouldContain("newer");
        (await StampedVersionAsync(connectionString)).ShouldBe(SchemaInfo.CurrentVersion + 1);
        (await ColumnCountAsync(connectionString, "dbo.SessionPeers", "LeftAt")).ShouldBe(0);
    }

    [Fact]
    public async Task DeployAsync_UnstampedDatabase_RefusedWithoutAdopt()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await ExecuteAsync(connectionString, "CREATE TABLE dbo.SomeoneElses (Id int NOT NULL)");

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            () => Deployer(connectionString).DeployAsync(DeployApproval.OperatorReviewed, allowDataLoss: false, adoptUnstamped: false, default));

        failure.Message.ShouldContain("--adopt-unstamped");
        (await ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM sys.tables WHERE name = N'Workspaces'")).ShouldBe(0);
    }

    [Fact]
    public async Task DeployAsync_UnstampedDatabase_WithAdopt_FollowsClassification()
    {
        // Foreign objects only: the schema is purely additive, so it is auto-safe and adopted ...
        var additive = await fixture.CreateDatabaseAsync();
        await ExecuteAsync(additive, "CREATE TABLE dbo.SomeoneElses (Id int NOT NULL)");

        // ... but not while it would also switch READ_COMMITTED_SNAPSHOT on under whoever is connected.
        var optionRefused = await Deployer(additive).DeployAsync(DeployApproval.AutoSafeOnly, false, adoptUnstamped: true, default);
        optionRefused.Applied.ShouldBeFalse();
        optionRefused.Reasons.ShouldContain(reason => reason.Contains("READ_COMMITTED_SNAPSHOT", StringComparison.Ordinal));
        await ExecuteAsync(additive, "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE");
        SqlConnection.ClearAllPools();

        var adopted = await Deployer(additive).DeployAsync(DeployApproval.AutoSafeOnly, false, adoptUnstamped: true, default);

        adopted.Applied.ShouldBeTrue();
        (await Deployer(additive).GetStatusAsync(default)).State.ShouldBe(SchemaState.Current);

        // A clashing table: the same name with another shape is not an additive change, so adopting still refuses.
        var clashing = await fixture.CreateDatabaseAsync();
        await ExecuteAsync(clashing, "CREATE TABLE dbo.Workspaces (Id int NOT NULL)");

        var refused = await Deployer(clashing).DeployAsync(DeployApproval.AutoSafeOnly, false, adoptUnstamped: true, default);

        refused.Applied.ShouldBeFalse();
        refused.Classification.ShouldNotBe(DeployClassification.AutoSafe);
        (await ScalarAsync<int>(clashing, "SELECT COUNT(*) FROM sys.tables WHERE name = N'Messages'")).ShouldBe(0);
    }

    [Fact]
    public async Task PostDeployMerge_NeverLowersVersion()
    {
        var connectionString = await DeployedDatabaseAsync();
        var script = File.ReadAllText(RepoPath("src", "DataLayer", "Nachos.DataLayer.SqlServer.Database", "Scripts", "Script.PostDeployment.sql"));

        await SetVersionAsync(connectionString, SchemaInfo.CurrentVersion + 4);
        await ExecuteAsync(connectionString, script);
        (await StampedVersionAsync(connectionString)).ShouldBe(SchemaInfo.CurrentVersion + 4);

        await SetVersionAsync(connectionString, 0);
        await ExecuteAsync(connectionString, script);
        (await StampedVersionAsync(connectionString)).ShouldBe(SchemaInfo.CurrentVersion);
    }

    // ---- approval versus data loss (I3) ----

    [Fact]
    public async Task UnsafeDiff_Refused()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "ALTER TABLE dbo.Workspaces ADD NotInTheModel int NULL");

        var report = await AutoSafeDeployAsync(connectionString);

        report.Classification.ShouldBe(DeployClassification.Unsafe);
        report.Applied.ShouldBeFalse();
        (await ColumnCountAsync(connectionString, "dbo.Workspaces", "NotInTheModel")).ShouldBe(1);
    }

    [Fact]
    public async Task OperatorReviewed_UnsafeDiff_StillBlocksDataLoss_UnlessAllowed()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "INSERT dbo.Workspaces (Name, LifecycleState, CreatedAt) VALUES (N'w', 0, SYSDATETIMEOFFSET())");
        await ExecuteAsync(connectionString, "ALTER TABLE dbo.Workspaces ADD NotInTheModel int NULL");
        var deployer = Deployer(connectionString);

        // Reviewed means "I have read the report", not "I accept losing data".
        await Should.ThrowAsync<DacServicesException>(
            () => deployer.DeployAsync(DeployApproval.OperatorReviewed, allowDataLoss: false, adoptUnstamped: false, default));
        (await ColumnCountAsync(connectionString, "dbo.Workspaces", "NotInTheModel")).ShouldBe(1);

        var report = await deployer.DeployAsync(DeployApproval.OperatorReviewed, allowDataLoss: true, adoptUnstamped: false, default);

        report.Applied.ShouldBeTrue();
        (await ColumnCountAsync(connectionString, "dbo.Workspaces", "NotInTheModel")).ShouldBe(0);
    }

    [Fact]
    public async Task AddNullableColumn_IsAutoSafe_AndApplied()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "ALTER TABLE dbo.SessionPeers DROP COLUMN LeftAt");
        await SetVersionAsync(connectionString, 0);
        (await Deployer(connectionString).ReportAsync(default)).Classification.ShouldBe(DeployClassification.AutoSafe);

        await Gate(connectionString, automatic: true).EnsureAsync(default);

        (await ColumnCountAsync(connectionString, "dbo.SessionPeers", "LeftAt")).ShouldBe(1);
        (await Deployer(connectionString).GetStatusAsync(default)).State.ShouldBe(SchemaState.Current);
        (await Deployer(connectionString).ReportAsync(default)).HasPendingChanges.ShouldBeFalse();
    }

    [Fact]
    public async Task AddNotNullColumnWithDefault_IsAutoSafe()
    {
        var connectionString = await DeployedDatabaseAsync();
        await SetVersionAsync(connectionString, 0);
        await ExecuteAsync(connectionString, "ALTER TABLE dbo.SchemaVersion DROP CONSTRAINT DF_SchemaVersion_AppliedAt; ALTER TABLE dbo.SchemaVersion DROP COLUMN AppliedAt");
        (await Deployer(connectionString).ReportAsync(default)).Classification.ShouldBe(DeployClassification.AutoSafe);

        await Gate(connectionString, automatic: true).EnsureAsync(default);

        (await ColumnCountAsync(connectionString, "dbo.SchemaVersion", "AppliedAt")).ShouldBe(1);
        (await StampedVersionAsync(connectionString)).ShouldBe(SchemaInfo.CurrentVersion);
    }

    [Fact]
    public async Task AddNotNullColumnWithoutDefault_IsUnsafe()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "DROP INDEX IX_IdempotencyRecords_ExpiresAt ON dbo.IdempotencyRecords; ALTER TABLE dbo.IdempotencyRecords DROP COLUMN ExpiresAt");

        var report = await AutoSafeDeployAsync(connectionString);

        report.Classification.ShouldBe(DeployClassification.Unsafe);
        report.Applied.ShouldBeFalse();
        (await ColumnCountAsync(connectionString, "dbo.IdempotencyRecords", "ExpiresAt")).ShouldBe(0);
    }

    [Fact]
    public async Task AlterColumnType_IsUnsafe()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "ALTER TABLE dbo.IdempotencyRecords ALTER COLUMN ResponseStatus bigint NOT NULL");

        var report = await AutoSafeDeployAsync(connectionString);

        report.Classification.ShouldBe(DeployClassification.Unsafe);
        report.Applied.ShouldBeFalse();
        (await ScalarAsync<string>(connectionString, "SELECT TYPE_NAME(system_type_id) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.IdempotencyRecords') AND name = N'ResponseStatus'"))
            .ShouldBe("bigint");
    }

    [Fact]
    public async Task AlterColumnNullability_IsUnsafe()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "ALTER TABLE dbo.SessionPeers ALTER COLUMN LeftAt datetimeoffset(7) NOT NULL");

        var report = await AutoSafeDeployAsync(connectionString);

        // DacFx raises no alert for this: only the generated script shows it is not an added column.
        report.Classification.ShouldBe(DeployClassification.Unsafe);
        report.Applied.ShouldBeFalse();
        (await ScalarAsync<bool>(connectionString, "SELECT is_nullable FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.SessionPeers') AND name = N'LeftAt'"))
            .ShouldBeFalse();
    }

    // ---- constraints on existing tables, database options (I1, I2) ----

    [Fact]
    public async Task ConstraintCreateOnExistingTable_IsUnsafe()
    {
        var connectionString = await DeployedDatabaseAsync();
        // The reviewer's repro: the CHECK is gone, a row that violates it arrives, and the version is behind.
        // DacFx would script ADD CONSTRAINT ... WITH NOCHECK, leave it untrusted, and the stamp would still say current.
        await ExecuteAsync(connectionString, "ALTER TABLE dbo.Sessions DROP CONSTRAINT CK_Sessions_LifecycleState");
        await ExecuteAsync(connectionString, "INSERT dbo.Workspaces (Name, LifecycleState, CreatedAt) VALUES (N'w', 0, SYSDATETIMEOFFSET())");
        await ExecuteAsync(connectionString, "INSERT dbo.Sessions (WorkspaceId, Name, LifecycleState, CreatedAt) SELECT Id, N's', 9, SYSDATETIMEOFFSET() FROM dbo.Workspaces");
        await SetVersionAsync(connectionString, 0);

        var report = await Deployer(connectionString).ReportAsync(default);
        report.Classification.ShouldBe(DeployClassification.Unsafe);

        var refused = await AutoSafeDeployAsync(connectionString);

        refused.Applied.ShouldBeFalse();
        (await ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM sys.check_constraints WHERE name = N'CK_Sessions_LifecycleState'")).ShouldBe(0);
        (await StampedVersionAsync(connectionString)).ShouldBe(0);
        var failure = await Should.ThrowAsync<InvalidOperationException>(() => Gate(connectionString, automatic: true).EnsureAsync(default));
        failure.Message.ShouldContain("nachos schema upgrade");
    }

    [Fact]
    public async Task FailedPostDeployVerification_RollsStampBack_GateRefusesAgain()
    {
        var connectionString = await DeployedDatabaseAsync();
        // The I1 repro: the CHECK is gone, a row that violates it arrives, and the version is behind. Applying the change
        // adds the constraint WITH NOCHECK, so the stamp moves to current while the schema still differs from the model.
        await ExecuteAsync(connectionString, "ALTER TABLE dbo.Sessions DROP CONSTRAINT CK_Sessions_LifecycleState");
        await ExecuteAsync(connectionString, "INSERT dbo.Workspaces (Name, LifecycleState, CreatedAt) VALUES (N'w', 0, SYSDATETIMEOFFSET())");
        await ExecuteAsync(connectionString, "INSERT dbo.Sessions (WorkspaceId, Name, LifecycleState, CreatedAt) SELECT Id, N's', 9, SYSDATETIMEOFFSET() FROM dbo.Workspaces");
        await SetVersionAsync(connectionString, 0);

        // DacFx's own validation of the unchecked constraint fails the deploy, after the post-deployment script has stamped it.
        await Should.ThrowAsync<DacServicesException>(
            () => Deployer(connectionString).DeployAsync(DeployApproval.OperatorReviewed, allowDataLoss: false, adoptUnstamped: false, default));

        (await StampedVersionAsync(connectionString)).ShouldBe(0);
        (await Deployer(connectionString).GetStatusAsync(default)).State.ShouldBe(SchemaState.Behind);

        // The failed deploy leaves the CHECK in place but unvalidated. DacFx's report cannot see that, so the deployer must.
        var report = await Deployer(connectionString).ReportAsync(default);
        report.Classification.ShouldBe(DeployClassification.Unsafe);
        report.HasPendingChanges.ShouldBeTrue();
        report.Reasons.ShouldContain(reason => reason.Contains("CK_Sessions_LifecycleState", StringComparison.Ordinal) && reason.Contains("not trusted", StringComparison.Ordinal));

        // Each gate is fresh, so none can be served a cached outcome.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var failure = await Should.ThrowAsync<InvalidOperationException>(() => Gate(connectionString, automatic: true).EnsureAsync(default));
            failure.Message.ShouldContain("nachos schema upgrade");
        }
    }
    [Fact]
    public async Task RcsiOff_ClassifiedUnsafe_NotApplied()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE");
        SqlConnection.ClearAllPools();
        await SetVersionAsync(connectionString, 0);
        var deployer = Deployer(connectionString);

        // DacFx's report says nothing about database options, but applying this one disconnects every session.
        var report = await deployer.ReportAsync(default);
        report.Classification.ShouldBe(DeployClassification.Unsafe);
        report.Reasons.ShouldContain(reason => reason.Contains("READ_COMMITTED_SNAPSHOT", StringComparison.Ordinal));

        (await AutoSafeDeployAsync(connectionString)).Applied.ShouldBeFalse();
        (await ScalarAsync<bool>(connectionString, "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()")).ShouldBeFalse();
        var failure = await Should.ThrowAsync<InvalidOperationException>(() => Gate(connectionString, automatic: true).EnsureAsync(default));
        failure.Message.ShouldContain("READ_COMMITTED_SNAPSHOT");
        failure.Message.ShouldContain("nachos schema upgrade");

        var reviewed = await deployer.DeployAsync(DeployApproval.OperatorReviewed, allowDataLoss: false, adoptUnstamped: false, default);

        reviewed.Applied.ShouldBeTrue();
        (await ScalarAsync<bool>(connectionString, "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()")).ShouldBeTrue();
    }

    // ---- schema facts ----

    [Fact]
    public async Task AllIndexKeys_WithinSqlLimits()
    {
        var connectionString = await DeployedDatabaseAsync();
        const string sql = """
            SELECT OBJECT_NAME(i.object_id) AS TableName, i.name AS IndexName, i.type AS IndexType,
                   SUM(CAST(c.max_length AS int)) AS KeyBytes
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            JOIN sys.tables t ON t.object_id = i.object_id AND t.is_ms_shipped = 0
            WHERE i.type IN (1, 2)
            GROUP BY i.object_id, i.index_id, i.name, i.type
            """;

        var checkedIndexes = 0;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            checkedIndexes++;
            var clustered = reader.GetByte(2) == 1;
            var limit = clustered ? 900 : 1700;
            reader.GetInt32(3).ShouldBeLessThanOrEqualTo(limit, $"{reader.GetString(0)}.{reader.GetString(1)} key size");
        }

        checkedIndexes.ShouldBeGreaterThan(10);
    }

    [Fact]
    public void PostDeployVersion_MatchesSchemaInfo()
    {
        var script = File.ReadAllText(RepoPath("src", "DataLayer", "Nachos.DataLayer.SqlServer.Database", "Scripts", "Script.PostDeployment.sql"));
        var marked = script.Split('\n').Single(line => line.Contains("SCHEMA VERSION LITERAL", StringComparison.Ordinal));

        var literal = SelectVersionLiteral().Match(marked);

        literal.Success.ShouldBeTrue($"no 'AS [Version]' literal on the marked line: {marked}");
        int.Parse(literal.Groups["version"].Value, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(SchemaInfo.CurrentVersion);
    }

    [GeneratedRegex(@"\b(?<version>\d+)\s+AS\s+\[Version\]", RegexOptions.IgnoreCase)]
    private static partial Regex SelectVersionLiteral();

    private static string RepoPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Nachos.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine([(directory ?? throw new InvalidOperationException("Nachos.slnx not found above the test output.")).FullName, .. parts]);
    }
}
