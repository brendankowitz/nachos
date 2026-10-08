using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.SqlServer.Dac;
using Nachos.Abstractions.Schema;
using Nachos.DataLayer.SqlServer.Schema;

namespace Nachos.Cli;

/// <summary>
/// <c>nachos schema status|report|upgrade</c>: thin wrappers over <see cref="SchemaDeployer"/>, which owns every rule about what may
/// be applied. The connection string goes to SqlClient untouched, so any authentication mode it supports works.
/// </summary>
internal static class SchemaCommands
{
    private static readonly JsonSerializerOptions StatusJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Command Create() => new("schema", "Inspect and upgrade the database schema.")
    {
        StatusCommand(),
        ReportCommand(),
        UpgradeCommand(),
    };

    private static Option<string> ConnectionOption() => new("--connection")
    {
        Description = "SQL Server connection string of the Nachos database (passed to SqlClient unchanged).",
        Required = true,
    };

    private static SchemaDeployer Deployer(string connectionString) =>
        new(new SqlServerOptions { ConnectionString = connectionString });

    private static Command StatusCommand()
    {
        var connection = ConnectionOption();
        var command = new Command("status", "Print the database's schema position as JSON.") { connection };
        command.SetAction((parse, ct) => CommandFailure.GuardAsync(parse.InvocationConfiguration.Error, async () =>
        {
            var status = await Deployer(parse.GetRequiredValue(connection)).GetStatusAsync(ct);
            await parse.InvocationConfiguration.Output.WriteLineAsync(JsonSerializer.Serialize(
                new { platform = status.Platform, deployed = status.Deployed, current = status.Current, state = status.State }, StatusJson));
            return ExitCodes.Success;
        }));
        return command;
    }

    private static Command ReportCommand()
    {
        var connection = ConnectionOption();
        var outFile = new Option<FileInfo>("--out") { Description = "Write the raw DacFx deploy report XML to this file." };
        var command = new Command("report", "Show what a deploy would change, without changing anything.") { connection, outFile };
        command.SetAction((parse, ct) => CommandFailure.GuardAsync(parse.InvocationConfiguration.Error, async () =>
        {
            var report = await Deployer(parse.GetRequiredValue(connection)).ReportAsync(ct);
            await WriteReportAsync(parse.InvocationConfiguration.Output, report, parse.GetValue(outFile), ct);
            return ExitCodes.Success;
        }));
        return command;
    }

    private static Command UpgradeCommand()
    {
        var connection = ConnectionOption();
        var reportOnly = new Option<bool>("--report-only") { Description = "Print the report and apply nothing." };
        var approveReviewed = new Option<bool>("--approve-reviewed")
        {
            Description = "An operator has read the report: apply unsafe and unclassifiable changes too (never data loss, unless --allow-data-loss).",
        };
        var allowDataLoss = new Option<bool>("--allow-data-loss") { Description = "Let the deploy drop data. Only valid with --approve-reviewed." };
        var adoptUnstamped = new Option<bool>("--adopt-unstamped")
        {
            Description = "Deploy into a database that has objects but no Nachos schema version.",
        };
        var command = new Command("upgrade", "Apply the schema. Exit 0: applied or nothing to do; 2: refused; 1: error.")
        {
            connection, reportOnly, approveReviewed, allowDataLoss, adoptUnstamped,
        };
        command.Validators.Add(result =>
        {
            if (result.GetValue(allowDataLoss) && !result.GetValue(approveReviewed))
            {
                result.AddError("--allow-data-loss is only valid together with --approve-reviewed.");
            }
        });
        command.SetAction((parse, ct) => CommandFailure.GuardAsync(parse.InvocationConfiguration.Error, () => UpgradeAsync(
            parse.InvocationConfiguration,
            Deployer(parse.GetRequiredValue(connection)),
            parse.GetValue(reportOnly),
            parse.GetValue(approveReviewed) ? DeployApproval.OperatorReviewed : DeployApproval.AutoSafeOnly,
            parse.GetValue(allowDataLoss),
            parse.GetValue(adoptUnstamped),
            ct)));
        return command;
    }

    private static async Task<int> UpgradeAsync(
        InvocationConfiguration io, SchemaDeployer manager, bool reportOnly, DeployApproval approval, bool allowDataLoss, bool adoptUnstamped, CancellationToken ct)
    {
        if (reportOnly)
        {
            await WriteReportAsync(io.Output, await manager.ReportAsync(ct), outFile: null, ct);
            return ExitCodes.Success;
        }

        // The deployer refuses these two with an exception that does not tell a refusal from a failure, so name them here.
        var status = await manager.GetStatusAsync(ct);
        if (status.State == SchemaState.Ahead)
        {
            await io.Error.WriteLineAsync(
                $"Refused: the database schema is version {status.Deployed}, newer than the version {status.Current} this build expects. " +
                "Nachos never downgrades a database; use a newer Nachos.");
            return ExitCodes.Refused;
        }

        if (status.State == SchemaState.Unstamped && !adoptUnstamped)
        {
            await io.Error.WriteLineAsync(
                "Refused: the database has objects but no Nachos schema version, so it was not created by Nachos. " +
                "Review 'nachos schema report', then run 'nachos schema upgrade --adopt-unstamped'.");
            return ExitCodes.Refused;
        }

        SchemaReport report;
        try
        {
            report = await manager.DeployAsync(approval, allowDataLoss, adoptUnstamped, ct);
        }
        catch (DacServicesException blocked) when (IsDataLossBlock(blocked))
        {
            await io.Error.WriteLineAsync($"Refused: {blocked.Message}");
            await io.Error.WriteLineAsync("Nothing was dropped. If the data may be lost, add --allow-data-loss (with --approve-reviewed).");
            return ExitCodes.Refused;
        }

        if (report.Applied)
        {
            await io.Output.WriteLineAsync("Schema upgraded.");
            return ExitCodes.Success;
        }

        if (!report.HasPendingChanges)
        {
            await io.Output.WriteLineAsync("Schema is current; nothing to do.");
            return ExitCodes.Success;
        }

        await io.Error.WriteLineAsync(
            $"Refused: the pending schema changes are classified {report.Classification} and need review. " +
            "Run 'nachos schema report --out <file>', read it, then run 'nachos schema upgrade --approve-reviewed'.");
        foreach (var reason in report.Reasons)
        {
            await io.Error.WriteLineAsync($"  - {reason}");
        }

        return ExitCodes.Refused;
    }

    // DacFx raises one exception type for every failure; its data-loss block is the only one that is a refusal.
    private static bool IsDataLossBlock(DacServicesException failure) =>
        failure.Message.Contains("data loss", StringComparison.OrdinalIgnoreCase);

    private static async Task WriteReportAsync(TextWriter output, SchemaReport report, FileInfo? outFile, CancellationToken ct)
    {
        if (outFile is not null)
        {
            await File.WriteAllTextAsync(outFile.FullName, report.ReportXml, ct);
        }

        await output.WriteLineAsync($"classification: {report.Classification}");
        await output.WriteLineAsync($"hasPendingChanges: {report.HasPendingChanges}");
        foreach (var reason in report.Reasons)
        {
            await output.WriteLineAsync($"reason: {reason}");
        }

        if (outFile is not null)
        {
            await output.WriteLineAsync($"report: {outFile.FullName}");
        }
    }
}
