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
    /// even if every caller stops waiting (a half-applied deploy is worse than a slow request). A success is
    /// remembered for the life of the process; a failure is forgotten the moment it happens, so the next call checks again.
    /// </summary>
    /// <exception cref="InvalidOperationException">The database needs a schema change this gate may not make. The message names <c>nachos schema upgrade</c>.</exception>
    public Task EnsureAsync(CancellationToken ct)
    {
        Task verification;
        lock (_sync)
        {
            // Task.Run: the failure handler below takes this lock, so it must not run before the task is stored.
            verification = _verification ??= Task.Run(VerifyAndForgetFailureAsync, CancellationToken.None);
        }

        return verification.WaitAsync(ct);
    }

    private async Task VerifyAndForgetFailureAsync()
    {
        try
        {
            await VerifyAsync(CancellationToken.None);
        }
        catch
        {
            lock (_sync)
            {
                _verification = null;
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

            case SchemaState.Empty when !options.AutomaticSchemaDeploymentEnabled:
                throw Refuse($"The database is empty and {SqlServerOptions.SectionName}:AutomaticSchemaDeploymentEnabled is false.");

            case SchemaState.Behind when !options.AutomaticSchemaDeploymentEnabled:
                throw Refuse($"The database schema is version {status.Deployed}, behind the version {status.Current} this build expects, and {SqlServerOptions.SectionName}:AutomaticSchemaDeploymentEnabled is false.");
        }

        var deployed = await schema.DeployAsync(DeployApproval.AutoSafeOnly, allowDataLoss: false, adoptUnstamped: false, ct);

        var after = await schema.GetStatusAsync(ct);
        if (after.State != SchemaState.Current)
        {
            throw Refuse(deployed.Applied
                ? $"The schema is still {after.State} after deploying."
                : $"The change to version {status.Current} is classified {deployed.Classification} and needs review. {string.Join(' ', deployed.Reasons)}");
        }

        // Current is only a stamp; the report proves the schema really matches.
        var verify = await schema.ReportAsync(ct);
        if (verify.HasPendingChanges)
        {
            throw Refuse($"The database is stamped current but still differs from the expected schema ({verify.Classification}). {string.Join(' ', verify.Reasons)}");
        }
    }

    private static InvalidOperationException Refuse(string reason) => new($"{reason} {Remedy}");
}