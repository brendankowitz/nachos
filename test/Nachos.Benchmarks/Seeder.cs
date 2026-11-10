using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;

namespace Nachos.Benchmarks;

/// <summary>
/// Writes messages and their chunk vectors straight into <c>dbo.Messages</c> and <c>dbo.MessageEmbeddings</c> with
/// <see cref="SqlBulkCopy"/>, in batches, for a workspace whose sessions and peers already exist (made through the API, so
/// every other row is what the product writes). Messages go round-robin over the sessions, alternating the session's two
/// peers, one second apart, so higher ids are newer. Half of the messages have two chunks; every chunk is synced.
/// </summary>
internal sealed class Seeder(string connectionString, Corpus corpus, int seed)
{
    private const int BatchSize = 10_000;

    private readonly Random _random = new(seed);
    private readonly float[] _vector = new float[Corpus.Dimensions];
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
    private long _nextMessageId = 1;
    private long _created;

    /// <summary>Messages written so far, in every workspace.</summary>
    public long Created => _created;

    /// <summary>Appends messages to <paramref name="workspace"/> until it holds <paramref name="target"/>.</summary>
    public async Task SeedAsync(SeedWorkspace workspace, long target, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        _nextMessageId = Math.Max(_nextMessageId, await ScalarAsync<long>(connection, "SELECT ISNULL(MAX(Id), 0) + 1 FROM dbo.Messages", ct));

        var clock = Stopwatch.StartNew();
        var startCount = workspace.Messages;
        while (workspace.Messages < target)
        {
            var count = (int)Math.Min(BatchSize, target - workspace.Messages);
            var (messages, chunks) = NextBatch(workspace, count);
            await CopyAsync(connection, "dbo.Messages", messages, SqlBulkCopyOptions.KeepIdentity, ct);
            await CopyAsync(connection, "dbo.MessageEmbeddings", chunks, SqlBulkCopyOptions.Default, ct);
            if (workspace.Messages % 100_000 == 0 || workspace.Messages == target)
            {
                var rate = (workspace.Messages - startCount) / Math.Max(clock.Elapsed.TotalSeconds, 0.001);
                Console.WriteLine($"  seeded {workspace.Name}: {workspace.Messages:N0} messages ({rate:N0}/s)");
            }
        }

        // The append path allocates seqs from this counter; keep it consistent with the seeded rows.
        await using var update = new SqlCommand(
            """
            UPDATE s SET NextMessageSeq = x.LastSeq + 1
            FROM dbo.Sessions AS s
            JOIN (SELECT SessionId, MAX(Seq) AS LastSeq FROM dbo.Messages WHERE WorkspaceId = @workspace GROUP BY SessionId) AS x
              ON x.SessionId = s.Id
            """, connection) { CommandTimeout = 0 };
        update.Parameters.AddWithValue("@workspace", workspace.Id);
        await update.ExecuteNonQueryAsync(ct);
    }

    private (DataTable Messages, DataTable Chunks) NextBatch(SeedWorkspace workspace, int count)
    {
        var messages = new DataTable();
        messages.Columns.Add("Id", typeof(long));
        messages.Columns.Add("WorkspaceId", typeof(long));
        messages.Columns.Add("SessionId", typeof(long));
        messages.Columns.Add("PeerId", typeof(long));
        messages.Columns.Add("PublicId", typeof(string));
        messages.Columns.Add("Seq", typeof(long));
        messages.Columns.Add("Content", typeof(string));
        messages.Columns.Add("TokenCount", typeof(int));
        messages.Columns.Add("Metadata", typeof(string));
        messages.Columns.Add("InternalMetadata", typeof(string));
        messages.Columns.Add("CreatedAt", typeof(DateTimeOffset));

        var chunks = new DataTable();
        chunks.Columns.Add("WorkspaceId", typeof(long));
        chunks.Columns.Add("MessageId", typeof(long));
        chunks.Columns.Add("SessionId", typeof(long));
        chunks.Columns.Add("ChunkIndex", typeof(short));
        chunks.Columns.Add("Content", typeof(string));
        chunks.Columns.Add("Embedding", typeof(SqlVector<float>));
        chunks.Columns.Add("SyncState", typeof(byte));
        chunks.Columns.Add("Attempts", typeof(int));
        chunks.Columns.Add("CreatedAt", typeof(DateTimeOffset));

        for (var i = 0; i < count; i++)
        {
            var session = workspace.Sessions[(int)(workspace.Messages % workspace.Sessions.Count)];
            var seq = ++session.LastSeq;
            var peer = seq % 2 == 0 ? session.SecondPeerId : session.FirstPeerId;
            var id = _nextMessageId++;
            var createdAt = _start.AddSeconds(_created++);
            var words = _random.Next(30, 71);
            var text = corpus.Text(_random, words);
            messages.Rows.Add(id, workspace.Id, session.Id, peer, $"bm{id:x20}", seq, text, words * 2, "{}", "{}", createdAt);

            var chunkTexts = _random.Next(2) == 0 ? [text] : Halves(text);
            for (short index = 0; index < chunkTexts.Length; index++)
            {
                Corpus.FillUnitVector(_random, _vector);
                chunks.Rows.Add(workspace.Id, id, session.Id, index, chunkTexts[index], new SqlVector<float>(_vector.ToArray()),
                    (byte)1, 0, createdAt);
            }

            workspace.Messages++;
        }

        return (messages, chunks);
    }

    private static string[] Halves(string text)
    {
        var middle = text.IndexOf(' ', text.Length / 2);
        return middle < 0 ? [text] : [text[..middle], text[(middle + 1)..]];
    }

    private static async Task CopyAsync(SqlConnection connection, string table, DataTable rows, SqlBulkCopyOptions options, CancellationToken ct)
    {
        // CheckConstraints keeps the foreign keys trusted, so the optimizer sees the same schema as a product-written database.
        using var copy = new SqlBulkCopy(connection, options | SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.TableLock, null)
        {
            DestinationTableName = table,
            BulkCopyTimeout = 0,
            BatchSize = BatchSize,
        };
        foreach (DataColumn column in rows.Columns)
        {
            copy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await copy.WriteToServerAsync(rows, ct);
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(ct))!;
    }
}

/// <summary>A seeded workspace: its database id, its sessions, and how many messages it holds.</summary>
internal sealed class SeedWorkspace(string name, long id, IReadOnlyList<SeedSession> sessions)
{
    public string Name { get; } = name;

    public long Id { get; } = id;

    public IReadOnlyList<SeedSession> Sessions { get; } = sessions;

    public long Messages { get; set; }
}

/// <summary>A seeded session with its two member peers (database ids) and the last seq written.</summary>
internal sealed class SeedSession(string name, long id, long firstPeerId, long secondPeerId)
{
    public string Name { get; } = name;

    public long Id { get; } = id;

    public long FirstPeerId { get; } = firstPeerId;

    public long SecondPeerId { get; } = secondPeerId;

    public long LastSeq { get; set; }
}
