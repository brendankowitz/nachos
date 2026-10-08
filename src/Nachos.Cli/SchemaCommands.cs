using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        try
        {
            var report = await manager.DeployAsync(approval, allowDataLoss, adoptUnstamped, ct);
            await io.Output.WriteLineAsync(report.Applied ? "Schema upgraded." : "Schema is current; nothing to do.");
            return ExitCodes.Success;
        }
        catch (SchemaDeployRefusedException refused)
        {
            // Only a typed refusal is exit 2. Anything else the deployer throws is a failure and falls through to exit 1.
            await io.Error.WriteLineAsync($"Refused: {refused.Message}");
            foreach (var reason in refused.Reasons)
            {
                await io.Error.WriteLineAsync($"  - {reason}");
            }

            if (Advice(refused, approval) is { } advice)
            {
                await io.Error.WriteLineAsync(advice);
            }

            return ExitCodes.Refused;
        }
    }

    // What to do next. Never names a flag the operator already passed. The deployer's own messages already say what to do
    // for Ahead and Unstamped.
    private static string? Advice(SchemaDeployRefusedException refused, DeployApproval approval)
    {
        var reviewed = approval == DeployApproval.OperatorReviewed ? "" : "--approve-reviewed ";
        return refused.Reason switch
        {
            SchemaRefusalReason.NotAutoSafe when refused.PossibleDataLoss =>
                "Run 'nachos schema report --out <file>' and read it. The changes could lose data; if that is acceptable, " +
                "re-run with --approve-reviewed --allow-data-loss.",
            SchemaRefusalReason.NotAutoSafe =>
                "Run 'nachos schema report --out <file>' and read it. If the changes are acceptable, re-run with --approve-reviewed.",
            SchemaRefusalReason.DataLossBlocked =>
                $"Nothing was changed. If the data may be lost, re-run with {reviewed}--allow-data-loss after reviewing.",
            _ => null,
        };
    }
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
