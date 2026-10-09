using Microsoft.EntityFrameworkCore;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.SqlServer.Storage;

namespace Nachos.DataLayer.SqlServer.Stores;

/// <remarks>
/// Object ids and roles are binary (case-exact, like the in-memory provider) and, like names, also compare
/// <c>DATALENGTH</c>: a binary collation still ignores trailing spaces in <c>=</c> and in the unique constraint.
/// </remarks>
internal sealed class SqlGrantStore(SqlStoreRuntime runtime) : IGrantStore
{
    private const string SameGrant =
        """
        ObjectId = @objectId AND DATALENGTH(ObjectId) = DATALENGTH(@objectId)
        AND Role = @role AND DATALENGTH(Role) = DATALENGTH(@role)
        AND ((@workspace IS NULL AND WorkspaceId IS NULL) OR WorkspaceId = @workspace)
        """;

    public async Task AddAsync(GrantRecord grant, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ColumnLimits.RequireGrant(grant.ObjectId, grant.Role);
        await using var db = await runtime.OpenAsync(ct);

        long? workspaceId = grant.WorkspaceName is { } workspace ? await Lookups.RequireWorkspaceIdAsync(db, workspace, ct) : null;
        try
        {
            // The unique constraint treats NULL workspaces as equal, so a concurrent duplicate add fails here and is a no-op.
            await db.Database.ExecuteSqlRawAsync(
                string.Concat(
                    "INSERT dbo.PrincipalGrants (ObjectId, WorkspaceId, Role) SELECT @objectId, @workspace, @role WHERE NOT EXISTS (SELECT 1 FROM dbo.PrincipalGrants WHERE ",
                    SameGrant,
                    ")"),
                Parameters(grant, workspaceId),
                ct);
        }
        catch (Exception ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            // Lost a race with an identical add: the grant exists, which is what was asked. Unless the row in the way differs
            // only by trailing spaces, which the unique constraint treats as equal: then this grant cannot be stored.
            var exists = await db.Database
                .SqlQueryRaw<int>(string.Concat("SELECT COUNT(*) AS [Value] FROM dbo.PrincipalGrants WHERE ", SameGrant), Parameters(grant, workspaceId))
                .SingleAsync(ct);
            if (exists == 0)
            {
                throw new NachosValidationException(
                    "The grant differs from an existing grant only by trailing spaces in its object id or role, which the SQL Server provider cannot store side by side.");
            }
        }
    }

    public async Task RemoveAsync(GrantRecord grant, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);

        long? workspaceId = null;
        if (grant.WorkspaceName is { } workspace)
        {
            if (await Lookups.FindWorkspaceIdAsync(db, workspace, ct) is not { } id)
            {
                return;
            }

            workspaceId = id;
        }

        await db.Database.ExecuteSqlRawAsync(
            string.Concat("DELETE dbo.PrincipalGrants WHERE ", SameGrant), Parameters(grant, workspaceId), ct);
    }

    public async Task<IReadOnlyList<GrantRecord>> ListAsync(string? objectId, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);

        var grants = db.PrincipalGrants.AsNoTracking();
        if (objectId is not null)
        {
            grants = grants.Where(g => g.ObjectId == objectId && EF.Functions.DataLength(g.ObjectId) == EF.Functions.DataLength(objectId));
        }

        var rows = await (
                from grant in grants
                join workspace in db.Workspaces on grant.WorkspaceId equals workspace.Id into scoped
                from workspace in scoped.DefaultIfEmpty()
                orderby grant.Id
                select new { grant.ObjectId, WorkspaceName = workspace == null ? null : workspace.Name, grant.Role })
            .ToListAsync(ct);
        return [.. rows.Select(row => new GrantRecord(row.ObjectId, row.WorkspaceName, row.Role))];
    }

    public async Task<WorkspaceGrants> GetWorkspaceGrantsAsync(string objectId, CancellationToken ct)
    {
        var all = false;
        var workspaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in await ListAsync(objectId, ct))
        {
            if (!string.Equals(grant.Role, GrantRoles.Workspace, StringComparison.Ordinal))
            {
                continue;
            }

            if (grant.WorkspaceName is null)
            {
                all = true;
            }
            else
            {
                workspaces.Add(grant.WorkspaceName);
            }
        }

        return new WorkspaceGrants(all, workspaces);
    }

    private static object[] Parameters(GrantRecord grant, long? workspaceId) =>
    [
        SqlParameters.Text("@objectId", grant.ObjectId),
        SqlParameters.Text("@role", grant.Role),
        workspaceId is { } id
            ? SqlParameters.Long("@workspace", id)
            : new Microsoft.Data.SqlClient.SqlParameter("@workspace", System.Data.SqlDbType.BigInt) { Value = DBNull.Value },
    ];
}
