using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using Nachos.Abstractions.Schema;

namespace Nachos.DataLayer.SqlServer.Schema;

/// <summary>
/// Deploys the embedded dacpac with DacFx. The dacpac is the single source of truth for DDL: this class never
/// writes DDL itself, and never sets <c>AllowIncompatiblePlatform</c>.
/// </summary>
/// <remarks>
/// <para>DacFx is synchronous, so its calls run on the thread pool. Reports and status reads take no lock; a deploy holds
/// the cross-process schema lock (see <see cref="SchemaLock"/>) from its first read to its last. No connection is held open
/// across DacFx: it may change a database option, which disconnects every session in the database.</para>
/// <para><b>Permissions on box SQL Server.</b> The lock is taken in <c>master</c>, so the login must be able to connect there:
/// through the <c>guest</c> user, which is enabled in <c>master</c> by default. A contained-database user, or a server with
/// <c>guest</c> disabled in <c>master</c>, cannot, and the deploy then fails with an <see cref="InvalidOperationException"/> (not a <see cref="SchemaDeployRefusedException"/>)
/// that says so; nothing has been changed at that point. Run the upgrade with a login that can.</para>
/// <para><b>Database options.</b> Reports and classification always script database options, so a difference between
/// the database and the model (<c>PAGE_VERIFY</c>, <c>TARGET_RECOVERY_TIME</c>, <c>READ_COMMITTED_SNAPSHOT</c>, …) shows up as an
/// unsafe change. Only a bootstrap of an empty database, or an operator-reviewed deploy, applies them.</para>
/// </remarks>
public sealed class SchemaDeployer(SqlServerOptions options) : ISchemaManager
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(60);

    private static readonly string[] SystemDatabases = ["master", "model", "msdb", "tempdb"];

    /// <inheritdoc />
    public async Task<SchemaStatus> GetStatusAsync(CancellationToken ct)
    {
        RequireNamedUserDatabase();
        return (await ReadAsync(ct)).Status;
    }

    /// <inheritdoc />
    public async Task<SchemaReport> ReportAsync(CancellationToken ct)
    {
        RequireNamedUserDatabase();
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

        RequireNamedUserDatabase();

        // The platform decides where the lock lives and the server's name for the database names it, so ask before taking it.
        var first = await ReadAsync(ct);
        await using var held = await SchemaLock.AcquireAsync(
            options.ConnectionString, first.DatabaseName, inMaster: first.Target != DacpacCatalog.Azure, LockTimeout, ct);

        // Everything below is decided from what the database looks like now, under the lock.
        var observed = await ReadAsync(ct);
        switch (observed.Status.State)
        {
            case SchemaState.Ahead:
                throw new SchemaDeployRefusedException(
                    SchemaRefusalReason.Ahead,
                    $"The database schema is version {observed.Status.Deployed}, newer than the version {observed.Status.Current} this build expects. " +
                    "Nachos never downgrades a database; deploy a newer Nachos instead.");
            case SchemaState.Unstamped when !adoptUnstamped:
                throw new SchemaDeployRefusedException(
                    SchemaRefusalReason.Unstamped,
                    "The database has objects but no Nachos schema version, so it was not created by Nachos and will not be changed. " +
                    "If it should be adopted, review 'nachos schema report', then run 'nachos schema upgrade --adopt-unstamped'.");
        }

        var report = await Task.Run(() => BuildReport(observed, ct), ct);
        if (observed.Status.State == SchemaState.Current && !report.HasPendingChanges)
        {
            return report;
        }

        // An empty database has nothing a deploy could damage, so bootstrapping it needs no review.
        var bootstrap = observed.Status.State == SchemaState.Empty;
        var mayApply = bootstrap
                       || approval == DeployApproval.OperatorReviewed
                       || report.Classification == DeployClassification.AutoSafe;
        var dataLoss = DeployReportClassifier.DataLossIssues(report.ReportXml);
        if (!mayApply)
        {
            throw new SchemaDeployRefusedException(
                SchemaRefusalReason.NotAutoSafe,
                $"The pending schema changes are classified {report.Classification} and have not been reviewed.",
                [.. report.Reasons, .. dataLoss],
                possibleDataLoss: dataLoss.Count > 0);
        }

        // DacFx would block this too, but only once it reaches a table that has rows, and only with an error that cannot be told
        // from a real failure. Refusing here is typed and happens before anything is changed.
        if (!allowDataLoss && dataLoss.Count > 0)
        {
            throw new SchemaDeployRefusedException(
                SchemaRefusalReason.DataLossBlocked,
                "The deploy could lose data, and data loss was not allowed.",
                dataLoss,
                possibleDataLoss: true);
        }

        try
        {
            // Database options are applied only on a bootstrap or when an operator has reviewed them. An auto-safe deploy
            // never touches them, even if the classification above were wrong.
            var scriptDatabaseOptions = bootstrap || approval == DeployApproval.OperatorReviewed;
            await Task.Run(() => Deploy(observed, allowDataLoss, scriptDatabaseOptions, ct), ct);

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
        // Database options are scripted even though an auto-safe deploy will not apply them: the report must show the drift.
        var deployOptions = DeployOptions(allowDataLoss: false, scriptDatabaseOptions: true);
        var service = new DacServices(options.ConnectionString);
        using var package = DacPackage.Load(observed.Target.Open());

        var xml = service.GenerateDeployReport(package, observed.DatabaseName, deployOptions, ct);

        // One analysis of the script serves the classifier and the checks below. An empty database is being bootstrapped,
        // so everything in its script is expected and nothing needs reading.
        var bootstrap = observed.Status.State == SchemaState.Empty;
        var script = new Lazy<DeployScriptAnalysis?>(
            () => DeployScriptAnalysis.TryParse(service.GenerateDeployScript(package, observed.DatabaseName, deployOptions, ct)));

        var classification = DeployReportClassifier.Classify(xml, DacpacModel.ConstraintTables(observed.Target), () => script.Value);
        var hasPendingChanges = DeployReportClassifier.HasOperations(xml);
        var reasons = new List<string>();

        void Escalate(DeployClassification to)
        {
            hasPendingChanges = true;
            if (classification == DeployClassification.AutoSafe)
            {
                classification = to;
            }
        }

        if (!bootstrap)
        {
            if (script.Value is not { } analysis)
            {
                reasons.Add("The deploy script could not be parsed, so what the deploy would do cannot be checked.");
                Escalate(DeployClassification.Unclassifiable);
            }
            else
            {
                if (analysis.DatabaseOptionChanges.Count > 0)
                {
                    // Changing one runs ALTER DATABASE ... WITH ROLLBACK IMMEDIATE, which disconnects every session.
                    reasons.Add(
                        $"Database option(s) {string.Join(", ", analysis.DatabaseOptionChanges)} differ from the schema and would be changed. Applying them runs " +
                        "ALTER DATABASE ... WITH ROLLBACK IMMEDIATE, which disconnects every session; run 'nachos schema upgrade --approve-reviewed' in a maintenance window.");
                    Escalate(DeployClassification.Unsafe);
                }

                if (analysis.DisallowedStatements.Count > 0)
                {
                    reasons.Add($"The deploy script contains statements outside the auto-safe allowlist: {string.Join(", ", analysis.DisallowedStatements)}.");
                    Escalate(DeployClassification.Unsafe);
                }

                if (analysis.HasOpaqueExecution)
                {
                    reasons.Add("The deploy script runs dynamic SQL or a procedure whose effect cannot be read from the script.");
                    Escalate(DeployClassification.Unclassifiable);
                }
            }
        }

        // DacFx compares definitions, not trust. A constraint added WITH NOCHECK over rows that violate it looks identical to
        // a sound one, so a deploy that failed half-way would otherwise read as a database with nothing left to do.
        if (observed.UntrustedConstraints.Count > 0)
        {
            reasons.Add(
                $"Constraint(s) exist but are not trusted (their rows were never validated): {string.Join(", ", observed.UntrustedConstraints)}. " +
                "Fix the offending rows, then validate with ALTER TABLE ... WITH CHECK CHECK CONSTRAINT, and re-run 'nachos schema report'.");
            Escalate(DeployClassification.Unsafe);
        }

        return new SchemaReport(classification, xml, Applied: false, hasPendingChanges, reasons);
    }

    private void Deploy(Observed observed, bool allowDataLoss, bool scriptDatabaseOptions, CancellationToken ct)
    {
        using var package = DacPackage.Load(observed.Target.Open());
        try
        {
            new DacServices(options.ConnectionString)
                .Deploy(package, observed.DatabaseName, upgradeExisting: true, DeployOptions(allowDataLoss, scriptDatabaseOptions), ct);
        }
        finally
        {
            // Changing a database option disconnects every session in the database, pooled idle ones included; drop them so
            // nothing reuses a dead connection.
            using var pooled = new SqlConnection(options.ConnectionString);
            SqlConnection.ClearPool(pooled);
        }
    }

    private static DacDeployOptions DeployOptions(bool allowDataLoss, bool scriptDatabaseOptions) => new()
    {
        BlockOnPossibleDataLoss = !allowDataLoss,
        ScriptDatabaseOptions = scriptDatabaseOptions,
    };

    // An early, connection-free refusal. It is only a convenience: the server's own database id decides (SchemaProbe).
    private void RequireNamedUserDatabase()
    {
        var name = new SqlConnectionStringBuilder(options.ConnectionString).InitialCatalog.Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("The connection string must name a database (Initial Catalog).");
        }

        if (SystemDatabases.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The connection string reaches the system database '{name}'. Nachos will not create or change its schema there; name a dedicated database.");
        }
    }

    private Task<Observed> ReadAsync(CancellationToken ct) => SchemaProbe.ReadAsync(options.ConnectionString, ct);
}
