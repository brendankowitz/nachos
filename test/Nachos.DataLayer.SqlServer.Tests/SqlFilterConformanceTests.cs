using Microsoft.Data.SqlClient;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.DataLayer.SqlServer.Storage;
using Nachos.Testing.Filtering;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>The shared filter cases, compiled by <c>SqlFilterCompiler</c> and run by SQL Server 2025 in Docker.</summary>
[Collection(SqlServerDockerGroup.Name)]
public sealed class SqlFilterConformanceTests(SqlServerFixture fixture) : FilterConformanceTests
{
    // Deliberately small, so every multi-row result is read across several pages.
    private const int PageSize = 3;

    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>
    /// Inserts the dataset directly (the public interfaces cannot set <c>CreatedAt</c>, public ids or former
    /// memberships), in one transaction, and does nothing if its first workspace already exists. Metadata goes through
    /// the provider's own JSON ingress, so it is stored exactly as the store would store it.
    /// </summary>
    protected override async Task SeedAsync(FilterDataset dataset)
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "filter-conformance");
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(Ct);

        if (await ScalarAsync<long?>(connection, transaction, "SELECT Id FROM dbo.Workspaces WHERE Name = @n", ("@n", dataset.Workspaces[0].Name)) is not null)
        {
            return;
        }

        var workspaceIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var workspace in dataset.Workspaces)
        {
            workspaceIds[workspace.Name] = await InsertAsync(
                connection,
                transaction,
                "INSERT dbo.Workspaces (Name, LifecycleState, Metadata, Configuration, CreatedAt) OUTPUT inserted.Id VALUES (@n, 0, @m, N'{}', @c)",
                ("@n", workspace.Name),
                ("@m", SqlJson.ToStorage(workspace.Metadata, "metadata")),
                ("@c", workspace.CreatedAt));
        }

        var scope = workspaceIds[dataset.ScopeWorkspace];
        var peerIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var peer in dataset.Peers)
        {
            peerIds[peer.Name] = await InsertAsync(
                connection,
                transaction,
                "INSERT dbo.Peers (WorkspaceId, Name, IsInternal, Metadata, Configuration, CreatedAt) OUTPUT inserted.Id VALUES (@w, @n, 0, @m, N'{}', @c)",
                ("@w", scope),
                ("@n", peer.Name),
                ("@m", SqlJson.ToStorage(peer.Metadata, "metadata")),
                ("@c", peer.CreatedAt));
        }

        var joined = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var sessionIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var session in dataset.Sessions)
        {
            var messageCount = dataset.Messages.Count(m => m.Session == session.Name);
            var sessionId = await InsertAsync(
                connection,
                transaction,
                "INSERT dbo.Sessions (WorkspaceId, Name, LifecycleState, NextMessageSeq, Metadata, Configuration, CreatedAt) OUTPUT inserted.Id VALUES (@w, @n, @s, @q, @m, N'{}', @c)",
                ("@w", scope),
                ("@n", session.Name),
                ("@s", (byte)(session.IsActive ? LifecycleState.Active : LifecycleState.Inactive)),
                ("@q", messageCount + 1L),
                ("@m", SqlJson.ToStorage(session.Metadata, "metadata")),
                ("@c", session.CreatedAt));
            sessionIds[session.Name] = sessionId;

            foreach (var member in session.Members)
            {
                await InsertAsync(
                    connection,
                    transaction,
                    "INSERT dbo.SessionPeers (WorkspaceId, SessionId, PeerId, Configuration, JoinedAt, LeftAt) OUTPUT inserted.PeerId VALUES (@w, @s, @p, N'{}', @j, @l)",
                    ("@w", scope),
                    ("@s", sessionId),
                    ("@p", peerIds[member.Peer]),
                    ("@j", joined),
                    ("@l", member.Active ? null : joined.AddDays(1)));
            }
        }

        var nextSeq = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var message in dataset.Messages)
        {
            var seq = nextSeq[message.Session] = nextSeq.GetValueOrDefault(message.Session) + 1;
            await InsertAsync(
                connection,
                transaction,
                "INSERT dbo.Messages (WorkspaceId, SessionId, PeerId, PublicId, Seq, Content, TokenCount, Metadata, CreatedAt) OUTPUT inserted.Id VALUES (@w, @s, @p, @i, @q, @t, @k, @m, @c)",
                ("@w", scope),
                ("@s", sessionIds[message.Session]),
                ("@p", peerIds[message.Peer]),
                ("@i", message.Id),
                ("@q", seq),
                ("@t", message.Content),
                ("@k", message.TokenCount),
                ("@m", SqlJson.ToStorage(message.Metadata, "metadata")),
                ("@c", message.CreatedAt));
        }

        await transaction.CommitAsync(Ct);
    }

    protected override async Task<IReadOnlyList<string>> QueryAsync(ResourceKind kind, FilterNode? filter)
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "filter-conformance");
        var store = database.CreateStore(TimeProvider.System);
        var dataset = FilterCaseLibrary.Dataset;
        var scope = dataset.ScopeWorkspace;
        switch (kind)
        {
            case ResourceKind.Workspace:
                return await CollectAsync((page, ct) => store.Workspaces.ListAsync(filter, page, ct), w => w.Name);
            case ResourceKind.Peer:
                return await CollectAsync((page, ct) => store.Peers.ListAsync(scope, PeerKind.All, filter, page, ct), p => p.Name);
            case ResourceKind.Session:
                return await CollectAsync((page, ct) => store.Sessions.ListAsync(scope, filter, page, ct), s => s.Name);
            default:
                var ids = new List<string>();
                foreach (var session in dataset.Sessions)
                {
                    ids.AddRange(await CollectAsync(
                        (page, ct) => store.Messages.ListAsync(scope, session.Name, filter, page, ct), m => m.PublicId));
                }

                return ids;
        }
    }

    private static async Task<List<string>> CollectAsync<T>(
        Func<PageRequest, CancellationToken, Task<Page<T>>> fetch, Func<T, string> id)
    {
        var ids = new List<string>();
        await foreach (var item in fetch.EnumerateAsync(PageSize))
        {
            ids.Add(id(item));
        }

        return ids;
    }

    private static async Task<long> InsertAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, params (string Name, object? Value)[] parameters) =>
        await ScalarAsync<long?>(connection, transaction, sql, parameters)
            ?? throw new InvalidOperationException("The insert returned no row.");

    private static async Task<T?> ScalarAsync<T>(
        SqlConnection connection, SqlTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = await command.ExecuteScalarAsync(Ct);
        return result is null or DBNull ? default : (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}
