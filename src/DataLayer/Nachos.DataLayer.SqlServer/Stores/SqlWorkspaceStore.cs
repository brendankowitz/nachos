using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.SqlServer.Filtering;
using Nachos.DataLayer.SqlServer.Storage;

namespace Nachos.DataLayer.SqlServer.Stores;

internal sealed class SqlWorkspaceStore(SqlStoreRuntime runtime) : IWorkspaceStore
{
    /// <remarks>Read, else insert; a lost insert race (2627/2601) re-reads the winner's committed row.</remarks>
    public async Task<WorkspaceRecord> GetOrCreateAsync(
        string name, JsonObject? metadata, JsonObject? configuration, CancellationToken ct)
    {
        runtime.Guard.ThrowIfReentered();
        var storedMetadata = SqlJson.ToStorage(metadata, JsonField.Metadata);
        var storedConfiguration = SqlJson.ToStorage(configuration, JsonField.Configuration);
        await using var db = await runtime.OpenAsync(ct);

        if (await db.Workspaces.AsNoTracking().Named(name).FirstOrDefaultAsync(ct) is { } existing)
        {
            return existing.ToRecord();
        }

        try
        {
            var inserted = await db.Workspaces
                .FromSqlRaw(
                    "INSERT dbo.Workspaces (Name, LifecycleState, Metadata, Configuration, CreatedAt) OUTPUT inserted.* VALUES (@name, 0, @metadata, @configuration, @now)",
                    SqlParameters.Text("@name", name),
                    SqlParameters.LongText("@metadata", storedMetadata),
                    SqlParameters.LongText("@configuration", storedConfiguration),
                    SqlParameters.Time("@now", runtime.Clock.GetUtcNow()))
                .AsNoTracking()
                .ToListAsync(ct);
            return inserted.Single().ToRecord();
        }
        catch (Exception ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            var winner = await db.Workspaces.AsNoTracking().Named(name).FirstOrDefaultAsync(ct)
                ?? throw Lookups.TrailingSpaceCollision("workspace", name);
            return winner.ToRecord();
        }
    }

    public async Task<WorkspaceRecord?> GetAsync(string name, CancellationToken ct)
    {
        runtime.Guard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);
        return (await db.Workspaces.AsNoTracking().Named(name).FirstOrDefaultAsync(ct))?.ToRecord();
    }

    public async Task<WorkspaceRecord> UpdateAsync(
        string name, JsonObject? metadata, JsonObject? configuration, CancellationToken ct)
    {
        runtime.Guard.ThrowIfReentered();
        var storedMetadata = SqlJson.ToStorageOptional(metadata, JsonField.Metadata);
        var storedConfiguration = SqlJson.ToStorageOptional(configuration, JsonField.Configuration);
        await using var db = await runtime.OpenAsync(ct);

        var updated = await JsonUpdate.ApplyAsync(
            db.Workspaces,
            "dbo.Workspaces",
            "Name = @name AND DATALENGTH(Name) = DATALENGTH(@name)",
            storedMetadata,
            storedConfiguration,
            [SqlParameters.Text("@name", name)],
            ct);
        return (updated ?? await db.Workspaces.AsNoTracking().Named(name).FirstOrDefaultAsync(ct)
            ?? throw Lookups.WorkspaceNotFound(name)).ToRecord();
    }

    public async Task<Page<WorkspaceRecord>> ListAsync(FilterNode? filter, PageRequest page, CancellationToken ct)
    {
        runtime.Guard.ThrowIfReentered();
        var (where, parameters) = SqlFilterCompiler.Compile(filter, ResourceKind.Workspace, "t");
        await using var db = await runtime.OpenAsync(ct);

        var rows = db.Workspaces.FromSqlRaw(string.Concat("SELECT * FROM dbo.Workspaces AS t WHERE ", where), [.. parameters]).AsNoTracking();
        return await Paging.ToPageAsync(
            rows,
            page,
            (query, reverse) => reverse
                ? query.OrderByDescending(w => w.CreatedAt).ThenByDescending(w => w.Id)
                : query.OrderBy(w => w.CreatedAt).ThenBy(w => w.Id),
            workspace => workspace.ToRecord(),
            ct);
    }
}
