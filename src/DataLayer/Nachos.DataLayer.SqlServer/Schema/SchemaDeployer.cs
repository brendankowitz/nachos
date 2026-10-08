using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using Nachos.Abstractions.Schema;

namespace Nachos.DataLayer.SqlServer.Schema;

/// <summary>
/// Deploys the embedded dacpac with DacFx. The dacpac is the single source of truth for DDL: this class never
/// writes DDL itself, and never sets <c>AllowIncompatiblePlatform</c>.
/// </summary>
/// <remarks>
/// DacFx is synchronous, so its calls run on the thread pool. Reports and status reads take no lock; a deploy holds
/// the cross-process schema lock (see <see cref="SchemaLock"/>) from its first read to its last. No connection is held open
/// across DacFx: it may change a database option, which disconnects every session in the database.
/// </remarks>
public sealed class SchemaDeployer(SqlServerOptions options) : ISchemaManager
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(60);

    private static readonly string[] SystemDatabases = ["master", "model", "msdb", "tempdb"];

    private const string ServerFactsSql = """
        SELECT CAST(SERVERPROPERTY('EngineEdition') AS int),
               CAST(SERVERPROPERTY('ProductMajorVersion') AS int),
               CASE WHEN EXISTS (SELECT 1 FROM sys.objects WHERE is_ms_shipped = 0) THEN 1 ELSE 0 END,
               CASE WHEN OBJECT_ID(N'dbo.SchemaVersion', N'U') IS NOT NULL
                     AND COL_LENGTH(N'dbo.SchemaVersion', N'Version') IS NOT NULL
                    THEN 1 ELSE 0 END,
               (SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID())
        """;

    private const string StampedVersionSql = "SELECT TOP (1) [Version] FROM [dbo].[SchemaVersion] WHERE [Id] = 1";

    private const string UntrustedConstraintsSql = """
        SELECT QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(parent_object_id)) + N'.' + QUOTENAME(name)
        FROM sys.check_constraints WHERE is_not_trusted = 1 AND is_disabled = 0 AND is_ms_shipped = 0
        UNION ALL
        SELECT QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(parent_object_id)) + N'.' + QUOTENAME(name)
        FROM sys.foreign_keys WHERE is_not_trusted = 1 AND is_disabled = 0 AND is_ms_shipped = 0
        """;

    private sealed record Observed(DacpacTarget Target, SchemaStatus Status, bool ReadCommittedSnapshotOn, IReadOnlyList<string> UntrustedConstraints);

    /// <inheritdoc />
    public async Task<SchemaStatus> GetStatusAsync(CancellationToken ct)
    {
        _ = DatabaseName();
        return (await ReadAsync(ct)).Status;
    }

    /// <inheritdoc />
    public async Task<SchemaReport> ReportAsync(CancellationToken ct)
    {
        _ = DatabaseName();
        var observed = await ReadAsync(ct);
        return await Task.Run(() => BuildReport(observed, ct), ct);
    }

    /// <inheritdoc />
    public async Task<SchemaReport> DeployAsync(DeployApproval approval, bool allowDataLoss, bool adoptUnstamped, CancellationToken ct)
    {
        if (allowDataLoss && approval != DeployApproval.OperatorReviewed)
        {
            throw new ArgumentException("Allowing data loss needs an operator-reviewed deploy.", nameof(allowDataLoss));
        }

        var database = DatabaseName();

        // The platform decides where the lock lives, so ask before taking it.
        var platform = (await ReadAsync(ct)).Target;
        await using var held = await SchemaLock.AcquireAsync(options.ConnectionString, database, inMaster: platform != DacpacCatalog.Azure, LockTimeout, ct);

        // Everything below is decided from what the database looks like now, under the lock.
        var observed = await ReadAsync(ct);
        switch (observed.Status.State)
        {
            case SchemaState.Ahead:
                throw new InvalidOperationException(
                    $"The database schema is version {observed.Status.Deployed}, newer than the version {observed.Status.Current} this build expects. " +
                    "Nachos never downgrades a database; deploy a newer Nachos instead.");
            case SchemaState.Unstamped when !adoptUnstamped:
                throw new InvalidOperationException(
                    "The database has objects but no Nachos schema version, so it was not created by Nachos and will not be changed. " +
                    "If it should be adopted, review 'nachos schema report', then run 'nachos schema upgrade --adopt-unstamped'.");
        }

        var report = await Task.Run(() => BuildReport(observed, ct), ct);
        if (observed.Status.State == SchemaState.Current && !report.HasPendingChanges)
        {
            return report;
        }

        // An empty database has nothing a deploy could damage, so bootstrapping it needs no review.
        var mayApply = observed.Status.State == SchemaState.Empty
                       || approval == DeployApproval.OperatorReviewed
                       || report.Classification == DeployClassification.AutoSafe;
        if (!mayApply)
        {
            return report;
        }

        try
        {
            await Task.Run(() => Deploy(observed.Target, allowDataLoss, ct), ct);

            var after = await ReadAsync(ct);
            var afterReport = await Task.Run(() => BuildReport(after, ct), ct);
            if (after.Status.State != SchemaState.Current || afterReport.HasPendingChanges)
            {
                throw new InvalidOperationException(
                    $"The deploy finished but the database is {after.Status.State} and still differs from the expected schema ({afterReport.Classification}). " +
                    "Run 'nachos schema report' and review the remaining changes.");
            }
        }
        catch (Exception failure)
        {
            // The post-deployment script stamps the new version before DacFx's own validation and our re-report run, so a
            // deploy that fails either would otherwise leave a stamp claiming a schema the database does not have, and every
            // later gate would take the Current no-op path. Put the stamp back while the lock is still held.
            await RollBackStampAsync(observed.Status.Deployed, failure);
            throw;
        }
        return report with { Applied = true };
    }

    // Restores the stamp the database had before the deploy: its old version, or no row at all if it had none (it then
    // reads Unstamped, so an operator must adopt it explicitly). The version is only ever lowered, never raised, and never
    // below what the database had before this deploy.
    private async Task RollBackStampAsync(int? previous, Exception cause)
    {
        try
        {
            await using var connection = new SqlConnection(options.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var command = new SqlCommand(
                previous is null
                    ? "IF OBJECT_ID(N'dbo.SchemaVersion', N'U') IS NOT NULL DELETE FROM [dbo].[SchemaVersion] WHERE [Id] = 1"
                    : "IF OBJECT_ID(N'dbo.SchemaVersion', N'U') IS NOT NULL UPDATE [dbo].[SchemaVersion] SET [Version] = @previous WHERE [Id] = 1 AND [Version] > @previous",
                connection);
            command.Parameters.AddWithValue("@previous", (object?)previous ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception rollbackFailure) when (rollbackFailure is SqlException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"The deploy failed ({cause.Message}) and the schema version stamp could not be restored ({rollbackFailure.Message}). " +
                "The database may be stamped current without matching the schema: run 'nachos schema report' and fix it before starting Nachos.",
                cause);
        }
    }
    private SchemaReport BuildReport(Observed observed, CancellationToken ct)
    {
        var database = DatabaseName();
        var deployOptions = DeployOptions(allowDataLoss: false);
        var service = new DacServices(options.ConnectionString);
        using var package = DacPackage.Load(observed.Target.Open());

        var xml = service.GenerateDeployReport(package, database, deployOptions, ct);
        var classification = DeployReportClassifier.Classify(
            xml,
            DacpacModel.ConstraintTables(observed.Target),
            () => service.GenerateDeployScript(package, database, deployOptions, ct));
        var hasPendingChanges = DeployReportClassifier.HasOperations(xml);

        // The report cannot show database options, but DacFx would change them (ALTER DATABASE ... WITH ROLLBACK IMMEDIATE,
        // which disconnects every session). Only a bootstrap, where nothing is connected yet, may do that unattended.
        var reasons = new List<string>();
        if (observed.Status.State != SchemaState.Empty && !observed.ReadCommittedSnapshotOn)
        {
            reasons.Add(
                "Database option READ_COMMITTED_SNAPSHOT is OFF but the schema expects ON. Applying it runs ALTER DATABASE ... WITH ROLLBACK IMMEDIATE, " +
                "which disconnects every session; run 'nachos schema upgrade --approve-reviewed' in a maintenance window.");
            hasPendingChanges = true;
            if (classification == DeployClassification.AutoSafe)
            {
                classification = DeployClassification.Unsafe;
            }
        }

        // DacFx compares definitions, not trust. A constraint added WITH NOCHECK over rows that violate it looks identical to
        // a sound one, so a deploy that failed half-way would otherwise read as a database with nothing left to do.
        if (observed.UntrustedConstraints.Count > 0)
        {
            reasons.Add(
                $"Constraint(s) exist but are not trusted (their rows were never validated): {string.Join(", ", observed.UntrustedConstraints)}. " +
                "Fix the offending rows, then validate with ALTER TABLE ... WITH CHECK CHECK CONSTRAINT, and re-run 'nachos schema report'.");
            hasPendingChanges = true;
            if (classification == DeployClassification.AutoSafe)
            {
                classification = DeployClassification.Unsafe;
            }
        }

        return new SchemaReport(classification, xml, Applied: false, hasPendingChanges, reasons);
    }

    private void Deploy(DacpacTarget target, bool allowDataLoss, CancellationToken ct)
    {
        using var package = DacPackage.Load(target.Open());
        try
        {
            new DacServices(options.ConnectionString)
                .Deploy(package, DatabaseName(), upgradeExisting: true, DeployOptions(allowDataLoss), ct);
        }
        finally
        {
            // Changing a database option (READ_COMMITTED_SNAPSHOT) disconnects every session in the database, pooled idle
            // ones included; drop them so nothing reuses a dead connection.
            using var pooled = new SqlConnection(options.ConnectionString);
            SqlConnection.ClearPool(pooled);
        }
    }

    private static DacDeployOptions DeployOptions(bool allowDataLoss) => new()
    {
        BlockOnPossibleDataLoss = !allowDataLoss,
        // Applies the project's database-level settings (read committed snapshot) to the existing database.
        // Reports flag an existing database whose options differ, so this only runs unattended on a bootstrap.
        ScriptDatabaseOptions = true,
    };

    private string DatabaseName()
    {
        var name = new SqlConnectionStringBuilder(options.ConnectionString).InitialCatalog;
        if (name.Length == 0)
        {
            throw new InvalidOperationException($"The connection string must name a database (Initial Catalog), at {SqlServerOptions.SectionName}:ConnectionString.");
        }

        return SystemDatabases.Contains(name, StringComparer.OrdinalIgnoreCase)
            ? throw new InvalidOperationException($"Nachos will not create its schema in the system database '{name}'. Name a dedicated database in {SqlServerOptions.SectionName}:ConnectionString.")
            : name;
    }

    private async Task<Observed> ReadAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(ct);

        int engineEdition, majorVersion;
        bool hasObjects, hasStamp, readCommittedSnapshotOn;
        await using (var command = new SqlCommand(ServerFactsSql, connection))
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct))
        {
            await reader.ReadAsync(ct);
            engineEdition = reader.GetInt32(0);
            majorVersion = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            hasObjects = reader.GetInt32(2) == 1;
            hasStamp = reader.GetInt32(3) == 1;
            readCommittedSnapshotOn = !reader.IsDBNull(4) && reader.GetBoolean(4);
        }

        var target = DacpacCatalog.Select(engineEdition, majorVersion);

        int? deployed = null;
        if (hasStamp)
        {
            await using var command = new SqlCommand(StampedVersionSql, connection);
            var stamped = await command.ExecuteScalarAsync(ct);
            deployed = stamped is null or DBNull ? null : Convert.ToInt32(stamped, System.Globalization.CultureInfo.InvariantCulture);
        }

        var state = (hasObjects, deployed) switch
        {
            (false, _) => SchemaState.Empty,
            (true, null) => SchemaState.Unstamped,
            (true, var v) when v == SchemaInfo.CurrentVersion => SchemaState.Current,
            (true, var v) when v < SchemaInfo.CurrentVersion => SchemaState.Behind,
            _ => SchemaState.Ahead,
        };

        var untrusted = new List<string>();
        if (hasObjects)
        {
            await using var command = new SqlCommand(UntrustedConstraintsSql, connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                untrusted.Add(reader.GetString(0));
            }
        }

        return new Observed(target, new SchemaStatus(target.Platform, deployed, SchemaInfo.CurrentVersion, state), readCommittedSnapshotOn, untrusted);
    }
}