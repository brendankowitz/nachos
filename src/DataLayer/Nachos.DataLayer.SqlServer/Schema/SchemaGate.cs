using Nachos.Abstractions.Schema;

namespace Nachos.DataLayer.SqlServer.Schema;

/// <summary>
/// Checks, once per process and before the first store use, that the database matches this build, and
/// applies the schema only where that is provably safe and the operator has enabled it.
/// </summary>
public sealed class SchemaGate(ISchemaManager schema, SqlServerOptions options)
{
    private const string Remedy = "Run 'nachos schema upgrade' (add --report-only to inspect the changes first).";

    private readonly object _sync = new();
    private Task? _verification;

    /// <summary>
    /// Returns when the database is current. Concurrent callers share one verification, which runs to completion
    /// even if a caller stops waiting (a half-applied deploy is worse than a slow request). A failed verification
    /// is not remembered: the next call checks again.
    /// </summary>
    /// <exception cref="InvalidOperationException">The database needs a schema change this gate may not make. The message names <c>nachos schema upgrade</c>.</exception>
    public async Task EnsureAsync(CancellationToken ct)
    {
        Task verification;
        lock (_sync)
        {
            verification = _verification ??= VerifyAsync(CancellationToken.None);
        }

        try
        {
            await verification.WaitAsync(ct);
        }
        catch when (verification.IsFaulted)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_verification, verification))
                {
                    _verification = null;
                }
            }

            throw;
        }
    }
    private async Task VerifyAsync(CancellationToken ct)
    {
        var status = await schema.GetStatusAsync(ct);
        switch (status.State)
        {
            case SchemaState.Current:
                return;

            case SchemaState.Unstamped:
                throw Refuse("The database has objects but no Nachos schema version, so Nachos will not change it automatically.");

            case SchemaState.Ahead:
                throw Refuse($"The database schema is version {status.Deployed}, newer than the version {status.Current} this build expects. Nachos never downgrades a database; upgrade the application.");

            case SchemaState.Empty:
                if (!options.AutomaticSchemaDeploymentEnabled)
                {
                    throw Refuse($"The database is empty and {SqlServerOptions.SectionName}:AutomaticSchemaDeploymentEnabled is false.");
                }

                await schema.DeployAsync(allowDataLoss: false, ct);
                break;

            case SchemaState.Behind:
                if (!options.AutomaticSchemaDeploymentEnabled)
                {
                    throw Refuse($"The database schema is version {status.Deployed}, behind the version {status.Current} this build expects, and {SqlServerOptions.SectionName}:AutomaticSchemaDeploymentEnabled is false.");
                }

                var report = await schema.ReportAsync(ct);
                if (report.Classification != DeployClassification.AutoSafe)
                {
                    throw Refuse($"The upgrade from version {status.Deployed} to {status.Current} is classified {report.Classification} and needs review.");
                }

                await schema.DeployAsync(allowDataLoss: false, ct);
                break;

            default:
                throw new InvalidOperationException($"Unhandled schema state {status.State}.");
        }

        var after = await schema.GetStatusAsync(ct);
        if (after.State != SchemaState.Current)
        {
            throw Refuse($"The schema is still {after.State} after deploying.");
        }
    }

    private static InvalidOperationException Refuse(string reason) => new($"{reason} {Remedy}");
}