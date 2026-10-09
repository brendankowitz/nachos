using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Schema;
using Nachos.Abstractions.Stores;
using Nachos.Core.Validation;
using Nachos.DataLayer.SqlServer;
using Nachos.DataLayer.SqlServer.Schema;

namespace Nachos.Cli;

/// <summary>
/// <c>nachos grants add|remove|list</c>: role grants that scope an object (an Entra object id) to workspaces, through the SQL store.
/// Grants never change the schema: a database whose schema is not current is refused (exit 2), and the operator is sent to
/// <c>nachos schema upgrade</c>.
/// </summary>
internal static class GrantCommands
{
    // The role names the domain knows (GrantRoles); case-sensitive, as stored.
    private static readonly string[] KnownRoles = [GrantRoles.Admin, GrantRoles.Workspace];

    // dbo.PrincipalGrants.ObjectId is NVARCHAR(64).
    private const int MaxObjectIdLength = 64;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    // Declaration order is the output's property order.
    private sealed record GrantJson(string ObjectId, string Role, string? Workspace);

    public static Command Create() => new("grants", "Manage the role grants that scope objects to workspaces.")
    {
        AddOrRemove("add", "Grant a role. Idempotent: prints {\"added\":true} whether or not the grant already existed.", add: true),
        AddOrRemove("remove", "Remove a grant. Idempotent: prints {\"removed\":true} whether or not the grant existed.", add: false),
        List(),
    };

    private static Option<string> ObjectIdOption(bool required) => new("--object-id")
    {
        Description = $"The object id (for example an Entra object id), up to {MaxObjectIdLength} characters.",
        Required = required,
    };

    private static Option<string> WorkspaceOption() => new("--workspace")
    {
        Description = "Limit the grant to this workspace. Omit it for a grant over all workspaces.",
    };

    private static Command AddOrRemove(string name, string description, bool add)
    {
        var connection = SchemaCommands.ConnectionOption();
        var objectId = ObjectIdOption(required: true);
        var role = new Option<string>("--role") { Description = $"The role: {string.Join(" or ", KnownRoles)}.", Required = true };
        var workspace = WorkspaceOption();
        var command = new Command(name, description + " Exit 0: done; 2: the schema is not current; 1: error.") { connection, objectId, role, workspace };
        command.Validators.Add(result =>
        {
            RequireObjectId(result, result.GetValue(objectId));
            RequireWorkspace(result, result.GetValue(workspace));
            if (result.GetValue(role) is { } roleName)
            {
                if (!KnownRoles.Contains(roleName, StringComparer.Ordinal))
                {
                    UsageErrors.Add(result, $"--role must be one of: {string.Join(", ", KnownRoles)}.");
                }
                else if (roleName == GrantRoles.Admin && result.GetValue(workspace) is not null)
                {
                    UsageErrors.Add(result, $"--workspace applies only to the {GrantRoles.Workspace} role; {GrantRoles.Admin} is not scoped to a workspace.");
                }
            }
        });
        command.SetAction((parse, ct) => SchemaCommands.GuardAsync(parse, connection, deployer => WithStoreAsync(parse, connection, deployer, async grants =>
        {
            var grant = new GrantRecord(parse.GetRequiredValue(objectId), parse.GetValue(workspace), parse.GetRequiredValue(role));
            if (add)
            {
                await grants.AddAsync(grant, ct);
                await parse.InvocationConfiguration.Output.WriteLineAsync("{\"added\":true}");
            }
            else
            {
                await grants.RemoveAsync(grant, ct);
                await parse.InvocationConfiguration.Output.WriteLineAsync("{\"removed\":true}");
            }

            return ExitCodes.Success;
        }, ct)));
        return command;
    }

    private static Command List()
    {
        var connection = SchemaCommands.ConnectionOption();
        var objectId = ObjectIdOption(required: false);
        var command = new Command("list", "Print the grants as a JSON array of {objectId, role, workspace}, sorted by those fields; workspace is null for all workspaces. Exit 0: done; 2: the schema is not current; 1: error.")
        {
            connection, objectId,
        };
        command.Validators.Add(result => RequireObjectId(result, result.GetValue(objectId)));
        command.SetAction((parse, ct) => SchemaCommands.GuardAsync(parse, connection, deployer => WithStoreAsync(parse, connection, deployer, async grants =>
        {
            var listed = await grants.ListAsync(parse.GetValue(objectId), ct);
            var rows = listed
                .Select(grant => new GrantJson(grant.ObjectId, grant.Role, grant.WorkspaceName))
                .OrderBy(row => row.ObjectId, StringComparer.Ordinal)
                .ThenBy(row => row.Role, StringComparer.Ordinal)
                .ThenBy(row => row.Workspace, StringComparer.Ordinal);
            await parse.InvocationConfiguration.Output.WriteLineAsync(JsonSerializer.Serialize(rows, Json));
            return ExitCodes.Success;
        }, ct)));
        return command;
    }

    // Messages name the option, never the value it was given.
    private static void RequireObjectId(CommandResult result, string? objectId)
    {
        if (objectId is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(objectId))
        {
            UsageErrors.Add(result, "--object-id must not be empty.");
        }
        else if (objectId.Length > MaxObjectIdLength)
        {
            UsageErrors.Add(result, $"--object-id must be at most {MaxObjectIdLength} characters.");
        }
    }

    private static void RequireWorkspace(CommandResult result, string? workspace)
    {
        if (workspace is null)
        {
            return;
        }

        try
        {
            IdValidator.Validate(workspace, "--workspace");
        }
        catch (NachosValidationException invalid)
        {
            UsageErrors.Add(result, invalid.Detail);
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> against the grant store once the schema is known to be current. The store's own schema gate stays
    /// at its default, which never deploys, so grants cannot change a database's schema.
    /// </summary>
    private static async Task<int> WithStoreAsync(
        ParseResult parse, Option<string> connection, SchemaDeployer deployer, Func<IGrantStore, Task<int>> body, CancellationToken ct)
    {
        var status = await deployer.GetStatusAsync(ct);
        if (status.State != SchemaState.Current)
        {
            await parse.InvocationConfiguration.Error.WriteLineAsync($"Refused: {NotCurrent(status)}");
            return ExitCodes.Refused;
        }

        var options = new SqlServerOptions { ConnectionString = parse.GetRequiredValue(connection) };
        var store = new SqlMemoryStore(options, new SchemaGate(deployer, options), TimeProvider.System);
        return await body(store.Grants);
    }

    private static string NotCurrent(SchemaStatus status) => status.State switch
    {
        SchemaState.Empty => "the database has no Nachos schema. Run 'nachos schema upgrade' first.",
        SchemaState.Unstamped => "the database has objects but no Nachos schema version, so it was not created by Nachos. Review 'nachos schema report', then run 'nachos schema upgrade --adopt-unstamped'.",
        SchemaState.Behind => $"the database schema is version {status.Deployed}, behind the version {status.Current} this build expects. Run 'nachos schema upgrade' first.",
        _ => $"the database schema is version {status.Deployed}, newer than the version {status.Current} this build expects. Nachos never downgrades a database; use a newer Nachos.",
    };
}
