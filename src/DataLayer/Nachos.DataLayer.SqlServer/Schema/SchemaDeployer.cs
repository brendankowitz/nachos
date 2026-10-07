using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using Nachos.Abstractions.Schema;

namespace Nachos.DataLayer.SqlServer.Schema;

/// <summary>
/// Deploys the embedded dacpac with DacFx. The dacpac is the single source of truth for DDL: this class never
/// writes DDL itself, and never sets <c>AllowIncompatiblePlatform</c>.
/// </summary>
/// <remarks>DacFx is synchronous, so its calls run on the thread pool.</remarks>
public sealed class SchemaDeployer(SqlServerOptions options) : ISchemaManager
{
    private const string ServerFactsSql = """
        SELECT CAST(SERVERPROPERTY('EngineEdition') AS int),
               CAST(SERVERPROPERTY('ProductMajorVersion') AS int),
               CASE WHEN EXISTS (SELECT 1 FROM sys.objects WHERE is_ms_shipped = 0) THEN 1 ELSE 0 END,
               CASE WHEN OBJECT_ID(N'dbo.SchemaVersion', N'U') IS NOT NULL
                     AND COL_LENGTH(N'dbo.SchemaVersion', N'Version') IS NOT NULL
                    THEN 1 ELSE 0 END
        """;

    private const string StampedVersionSql = "SELECT TOP (1) [Version] FROM [dbo].[SchemaVersion] WHERE [Id] = 1";

    /// <inheritdoc />
    public async Task<SchemaStatus> GetStatusAsync(CancellationToken ct) => (await ReadAsync(ct)).Status;

    /// <inheritdoc />
    public async Task<SchemaReport> ReportAsync(CancellationToken ct)
    {
        var observed = await ReadAsync(ct);
        return await Task.Run(() => Report(observed.Target, allowDataLoss: false, ct), ct);
    }

    /// <inheritdoc />
    public async Task<SchemaReport> DeployAsync(bool allowDataLoss, CancellationToken ct)
    {
        var observed = await ReadAsync(ct);
        return await Task.Run(() =>
        {
            var report = Report(observed.Target, allowDataLoss, ct);

            // An empty database has nothing a deploy could destroy, so it never needs the allowlist.
            var mayApply = allowDataLoss
                           || observed.Status.State == SchemaState.Empty
                           || report.Classification == DeployClassification.AutoSafe;
            if (mayApply)
            {
                Deploy(observed.Target, allowDataLoss, ct);
            }

            return report;
        }, ct);
    }

    private SchemaReport Report(DacpacTarget target, bool allowDataLoss, CancellationToken ct)
    {
        using var package = DacPackage.Load(target.Open());
        var xml = new DacServices(options.ConnectionString)
            .GenerateDeployReport(package, DatabaseName(), DeployOptions(allowDataLoss), ct);
        return new SchemaReport(DeployReportClassifier.Classify(xml), xml);
    }

    private void Deploy(DacpacTarget target, bool allowDataLoss, CancellationToken ct)
    {
        using var package = DacPackage.Load(target.Open());
        new DacServices(options.ConnectionString)
            .Deploy(package, DatabaseName(), upgradeExisting: true, DeployOptions(allowDataLoss), ct);
    }

    private static DacDeployOptions DeployOptions(bool allowDataLoss) => new()
    {
        BlockOnPossibleDataLoss = !allowDataLoss,
        // Applies the project's database-level settings (read committed snapshot) to the existing database.
        ScriptDatabaseOptions = true,
    };

    private string DatabaseName()
    {
        var name = new SqlConnectionStringBuilder(options.ConnectionString).InitialCatalog;
        return name.Length > 0
            ? name
            : throw new InvalidOperationException($"The connection string must name a database (Initial Catalog), at {SqlServerOptions.SectionName}:ConnectionString.");
    }

    private async Task<(DacpacTarget Target, SchemaStatus Status)> ReadAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(ct);

        int engineEdition, majorVersion;
        bool hasObjects, hasStamp;
        await using (var command = new SqlCommand(ServerFactsSql, connection))
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct))
        {
            await reader.ReadAsync(ct);
            engineEdition = reader.GetInt32(0);
            majorVersion = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            hasObjects = reader.GetInt32(2) == 1;
            hasStamp = reader.GetInt32(3) == 1;
        }

        var target = DacpacCatalog.Select(engineEdition, majorVersion);

        int? deployed = null;
        if (hasStamp)
        {
            await using var command = new SqlCommand(StampedVersionSql, connection);
            deployed = await command.ExecuteScalarAsync(ct) as int?;
        }

        var state = (hasObjects, deployed) switch
        {
            (false, _) => SchemaState.Empty,
            (true, null) => SchemaState.Unstamped,
            (true, var v) when v == SchemaInfo.CurrentVersion => SchemaState.Current,
            (true, var v) when v < SchemaInfo.CurrentVersion => SchemaState.Behind,
            _ => SchemaState.Ahead,
        };

        return (target, new SchemaStatus(target.Platform, deployed, SchemaInfo.CurrentVersion, state));
    }
}