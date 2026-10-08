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

    /// <summary>Every database option Nachos declares (or could clobber), as SQL Server reports it.</summary>
    private static async Task<SortedDictionary<string, string>> DatabaseOptionsAsync(string connectionString)
    {
        var options = new SortedDictionary<string, string>(StringComparer.Ordinal);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var sql in new[]
                 {
                     """
                     SELECT page_verify_option_desc, target_recovery_time_in_seconds, is_broker_enabled, is_ansi_null_default_on, is_ansi_nulls_on,
                            is_ansi_padding_on, is_ansi_warnings_on, is_arithabort_on, is_concat_null_yields_null_on, is_quoted_identifier_on,
                            is_numeric_roundabort_on, is_local_cursor_default, is_cursor_close_on_commit_on, is_recursive_triggers_on,
                            is_auto_close_on, is_auto_shrink_on, is_auto_create_stats_on, is_auto_update_stats_on, is_trustworthy_on,
                            is_db_chaining_on, is_parameterization_forced, snapshot_isolation_state_desc, recovery_model_desc,
                            compatibility_level, delayed_durability_desc, is_read_committed_snapshot_on
                     FROM sys.databases WHERE database_id = DB_ID()
                     """,
                     """
                     SELECT desired_state_desc, query_capture_mode_desc, stale_query_threshold_days, max_storage_size_mb, flush_interval_seconds,
                            interval_length_minutes, max_plans_per_query, size_based_cleanup_mode_desc
                     FROM sys.database_query_store_options
                     """,
                 })
        {
            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                options[reader.GetName(i)] = Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;
            }
        }

        return options;
    }

    private static async Task SetDatabaseOptionAsync(string connectionString, string option)
    {
        await ExecuteAsync(connectionString, $"ALTER DATABASE CURRENT SET {option} WITH ROLLBACK IMMEDIATE");
        SqlConnection.ClearAllPools();
    }

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
    public async Task Bootstrap_KeepsSafeDatabaseOptions_AndChangesOnlyReadCommittedSnapshot()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var before = await DatabaseOptionsAsync(connectionString);

        await AutoSafeDeployAsync(connectionString);
        var after = await DatabaseOptionsAsync(connectionString);

        after["page_verify_option_desc"].ShouldBe("CHECKSUM");
        after["target_recovery_time_in_seconds"].ShouldBe("60");
        after["is_broker_enabled"].ShouldBe("True");
        after["desired_state_desc"].ShouldBe("READ_WRITE");
        after["query_capture_mode_desc"].ShouldBe("AUTO");
        // A new database's own options are what the dacpac declares, so the only thing a bootstrap changes is the one Nachos needs.
        after.Where(option => before[option.Key] != option.Value).Select(option => option.Key)
            .ShouldBe(["is_read_committed_snapshot_on"]);
    }

    [Fact]
    public async Task ConnectionStringCaseDiffers_DeploysUnderTheServersName()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var shouted = new SqlConnectionStringBuilder(connectionString);
        shouted.InitialCatalog = shouted.InitialCatalog.ToUpperInvariant();

        var report = await AutoSafeDeployAsync(shouted.ConnectionString);

        report.Applied.ShouldBeTrue();
        (await Deployer(connectionString).GetStatusAsync(default)).State.ShouldBe(SchemaState.Current);
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
        report.Reasons.ShouldBeEmpty();
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
        var untouched = await DatabaseOptionsAsync(additive);
        var optionRefused = await Deployer(additive).DeployAsync(DeployApproval.AutoSafeOnly, false, adoptUnstamped: true, default);
        optionRefused.Applied.ShouldBeFalse();
        optionRefused.Reasons.ShouldContain(reason => reason.Contains("READ_COMMITTED_SNAPSHOT", StringComparison.Ordinal));
        (await DatabaseOptionsAsync(additive)).ShouldBe(untouched);
        (await ScalarAsync<int>(additive, "SELECT COUNT(*) FROM sys.tables WHERE name = N'Workspaces'")).ShouldBe(0);

        // Adopting must not touch the options of a database that is not Nachos's: only the ones the operator already matched.
        await SetDatabaseOptionAsync(additive, "READ_COMMITTED_SNAPSHOT ON");
        var optionsBeforeAdopt = await DatabaseOptionsAsync(additive);

        var adopted = await Deployer(additive).DeployAsync(DeployApproval.AutoSafeOnly, false, adoptUnstamped: true, default);

        adopted.Applied.ShouldBeTrue();
        (await Deployer(additive).GetStatusAsync(default)).State.ShouldBe(SchemaState.Current);
        (await DatabaseOptionsAsync(additive)).ShouldBe(optionsBeforeAdopt);

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
    [Theory]
    [InlineData("PAGE_VERIFY NONE", "PAGE_VERIFY")]
    [InlineData("TARGET_RECOVERY_TIME = 0 SECONDS", "TARGET_RECOVERY_TIME")]
    [InlineData("QUERY_STORE (QUERY_CAPTURE_MODE = ALL)", "QUERY_STORE")]
    [InlineData("ANSI_NULLS ON", "ANSI_NULLS")]
    public async Task BehindDatabase_DatabaseOptionDrift_IsUnsafe_AndNeverAppliedUnattended(string drift, string option)
    {
        var connectionString = await DeployedDatabaseAsync();
        await SetDatabaseOptionAsync(connectionString, drift);
        await SetVersionAsync(connectionString, 0);
        var drifted = await DatabaseOptionsAsync(connectionString);

        // The repro: a lowered stamp used to be enough for the gate to flip the setting back, with ROLLBACK IMMEDIATE, unattended.
        var report = await AutoSafeDeployAsync(connectionString);
        var failure = await Should.ThrowAsync<InvalidOperationException>(() => Gate(connectionString, automatic: true).EnsureAsync(default));

        report.Applied.ShouldBeFalse();
        report.Classification.ShouldBe(DeployClassification.Unsafe);
        report.HasPendingChanges.ShouldBeTrue();
        report.Reasons.ShouldContain(reason => reason.Contains(option, StringComparison.Ordinal));
        failure.Message.ShouldContain(option);
        failure.Message.ShouldContain("nachos schema upgrade");
        (await DatabaseOptionsAsync(connectionString)).ShouldBe(drifted);
        (await StampedVersionAsync(connectionString)).ShouldBe(0);

        // Reviewed by an operator, the same deploy does put it right.
        var reviewed = await Deployer(connectionString).DeployAsync(DeployApproval.OperatorReviewed, false, false, default);

        reviewed.Applied.ShouldBeTrue();
        (await DatabaseOptionsAsync(connectionString)).ShouldBe(await DatabaseOptionsAsync(await DeployedDatabaseAsync()));
    }

    [Fact]
    public async Task CurrentDatabase_DatabaseOptionDrift_IsPendingAndUnsafe()
    {
        var connectionString = await DeployedDatabaseAsync();
        await SetDatabaseOptionAsync(connectionString, "PAGE_VERIFY NONE");

        var report = await Deployer(connectionString).ReportAsync(default);
        var refused = await AutoSafeDeployAsync(connectionString);

        report.HasPendingChanges.ShouldBeTrue();
        report.Classification.ShouldBe(DeployClassification.Unsafe);
        refused.Applied.ShouldBeFalse();
        (await DatabaseOptionsAsync(connectionString))["page_verify_option_desc"].ShouldBe("NONE");
    }

    [Fact]
    public async Task MissingTable_IsAutoSafe_AndApplied_ThroughTheStatementAllowlist()
    {
        var connectionString = await DeployedDatabaseAsync();
        await ExecuteAsync(connectionString, "ALTER TABLE dbo.IdempotencyRecords DROP CONSTRAINT FK_IdempotencyRecords_Workspaces; DROP TABLE dbo.IdempotencyRecords");
        await SetVersionAsync(connectionString, 0);
        var report = await Deployer(connectionString).ReportAsync(default);
        report.Classification.ShouldBe(DeployClassification.AutoSafe);
        report.Reasons.ShouldBeEmpty();

        await Gate(connectionString, automatic: true).EnsureAsync(default);

        (await ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM sys.tables WHERE name = N'IdempotencyRecords'")).ShouldBe(1);
        // CREATE TABLE, CREATE INDEX and the NOCHECK foreign key were all allowed, and the key was validated afterwards.
        (await ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM sys.foreign_keys WHERE is_not_trusted = 1")).ShouldBe(0);
    }

    // ---- the system-database guard and the lock name ----

    [Theory]
    [InlineData("master")]
    [InlineData("master ")]
    [InlineData("MASTER")]
    [InlineData("tempdb ")]
    [InlineData("MSDB ")]
    [InlineData("model   ")]
    public async Task SystemDatabase_ReachedByAnySpelling_IsRefusedByTheServersOwnId(string spelling)
    {
        var connectionString = new SqlConnectionStringBuilder(await fixture.CreateDatabaseAsync()) { InitialCatalog = spelling }.ConnectionString;

        // The client-side name check is not involved: SchemaProbe asks the server which database it really reached.
        var failure = await Should.ThrowAsync<InvalidOperationException>(() => SchemaProbe.ReadAsync(connectionString, default));

        failure.Message.ShouldContain("system database");
        failure.Message.ShouldContain(spelling.Trim().ToLowerInvariant(), Case.Insensitive);
    }

    [Fact]
    public async Task LockIsNamedByTheServersDatabaseName_SoCasingCannotSplitIt()
    {
        var lower = await fixture.CreateDatabaseAsync();
        var shouted = new SqlConnectionStringBuilder(lower);
        shouted.InitialCatalog = shouted.InitialCatalog.ToUpperInvariant();

        var fromLower = await SchemaProbe.ReadAsync(lower, default);
        var fromShouted = await SchemaProbe.ReadAsync(shouted.ConnectionString, default);
        fromShouted.DatabaseName.ShouldBe(fromLower.DatabaseName);

        await using var held = await SchemaLock.AcquireAsync(lower, fromLower.DatabaseName, inMaster: true, TimeSpan.FromSeconds(30), default);

        await Should.ThrowAsync<TimeoutException>(
            () => SchemaLock.AcquireAsync(shouted.ConnectionString, fromShouted.DatabaseName, inMaster: true, TimeSpan.FromSeconds(1), default));
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
