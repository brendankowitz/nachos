using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.SqlServer.Storage;

namespace Nachos.DataLayer.SqlServer.Stores;

/// <remarks>Entries are stored as a JSON array of strings; a set replaces the whole card (update, else insert).</remarks>
internal sealed class SqlPeerCardStore(SqlStoreRuntime runtime) : IPeerCardStore
{
    public async Task<PeerCardRecord?> GetAsync(string workspaceName, string observerName, string observedName, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);
        var workspaceId = await Lookups.RequireWorkspaceIdAsync(db, workspaceName, ct);
        var row = (await db.Database
                .SqlQueryRaw<CardRow>(
                    """
                    SELECT c.Entries, c.UpdatedAt, c.UpdatedBy
                    FROM dbo.PeerCards AS c
                    JOIN dbo.Peers AS o ON o.Id = c.ObserverPeerId
                    JOIN dbo.Peers AS d ON d.Id = c.ObservedPeerId
                    WHERE c.WorkspaceId = @workspace
                      AND o.Name = @observer AND DATALENGTH(o.Name) = DATALENGTH(@observer)
                      AND d.Name = @observed AND DATALENGTH(d.Name) = DATALENGTH(@observed)
                    """,
                    SqlParameters.Long("@workspace", workspaceId),
                    SqlParameters.Text("@observer", observerName),
                    SqlParameters.Text("@observed", observedName))
                .ToListAsync(ct))
            .SingleOrDefault();
        return row is null
            ? null
            : new PeerCardRecord(
                workspaceName,
                observerName,
                observedName,
                JsonSerializer.Deserialize<string[]>(row.Entries) ?? throw new InvalidOperationException("A stored peer card is JSON null."),
                row.UpdatedAt,
                (PeerCardAuthor)row.UpdatedBy);
    }

    /// <remarks>Concurrent first sets race on the unique key; the loser of the insert then updates the winner's row.</remarks>
    public async Task<PeerCardRecord> SetAsync(
        string workspaceName,
        string observerName,
        string observedName,
        IReadOnlyList<string> entries,
        PeerCardAuthor updatedBy,
        CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(entries);
        string[] owned = [.. entries];
        var stored = JsonSerializer.Serialize(owned);
        await using var db = await runtime.OpenAsync(ct);
        var now = runtime.Clock.GetUtcNow();
        var workspaceId = await Lookups.RequireWorkspaceIdAsync(db, workspaceName, ct);
        var observer = await RequirePeerIdAsync(db, workspaceId, workspaceName, observerName, ct);
        var observed = await RequirePeerIdAsync(db, workspaceId, workspaceName, observedName, ct);

        object[] parameters =
        [
            SqlParameters.Long("@workspace", workspaceId),
            SqlParameters.Long("@observer", observer),
            SqlParameters.Long("@observed", observed),
            SqlParameters.LongText("@entries", stored),
            SqlParameters.Time("@now", now),
            SqlParameters.Byte("@by", (byte)updatedBy),
        ];
        const string Update = """
            UPDATE dbo.PeerCards SET Entries = @entries, UpdatedAt = @now, UpdatedBy = @by
            WHERE WorkspaceId = @workspace AND ObserverPeerId = @observer AND ObservedPeerId = @observed
            """;
        if (await db.Database.ExecuteSqlRawAsync(Update, parameters, ct) == 0)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT dbo.PeerCards (WorkspaceId, ObserverPeerId, ObservedPeerId, Entries, UpdatedAt, UpdatedBy)
                    VALUES (@workspace, @observer, @observed, @entries, @now, @by)
                    """,
                    parameters,
                    ct);
            }
            catch (Exception ex) when (SqlErrors.IsUniqueViolation(ex))
            {
                await db.Database.ExecuteSqlRawAsync(Update, parameters, ct);
            }
        }

        return new PeerCardRecord(workspaceName, observerName, observedName, [.. owned], now, updatedBy);
    }

    private static async Task<long> RequirePeerIdAsync(
        NachosDbContext db, long workspaceId, string workspaceName, string peerName, CancellationToken ct) =>
        await db.Peers.Named(workspaceId, peerName).Select(p => (long?)p.Id).FirstOrDefaultAsync(ct)
            ?? throw Lookups.PeerNotFound(workspaceName, peerName);

    private sealed record CardRow(string Entries, DateTimeOffset UpdatedAt, byte UpdatedBy);
}
