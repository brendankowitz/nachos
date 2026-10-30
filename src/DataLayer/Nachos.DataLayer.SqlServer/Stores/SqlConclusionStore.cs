using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.SqlServer.Entities;
using Nachos.DataLayer.SqlServer.Filtering;
using Nachos.DataLayer.SqlServer.Storage;

namespace Nachos.DataLayer.SqlServer.Stores;

/// <remarks>
/// <para>
/// <b>Reads</b> see live rows only (<c>DeletedAt IS NULL</c>); vector reads also need <c>EmbeddingState = 1</c> (synced).
/// Vector search is exact: <c>VECTOR_DISTANCE('cosine', …)</c> over the rows the other conditions leave, nearest first,
/// ties oldest first. <c>SourceIds</c> are the public ids of the workspace's conclusions named by <c>ConclusionSources</c>;
/// an id whose conclusion has been purged (the column has no FK by design) is left out.
/// </para>
/// <para>
/// <b>Writes</b> validate caller input (vectors, JSON) before the first round trip and run in one transaction, so an
/// exception leaves nothing behind. Every write of <c>Content</c> also writes <see cref="ContentHash"/>.
/// </para>
/// </remarks>
internal sealed class SqlConclusionStore(SqlStoreRuntime runtime) : IConclusionStore
{
    private const string SelectColumns = """
        SELECT c.Id, c.PublicId, o.Name AS ObserverName, d.Name AS ObservedName, s.Name AS SessionName, c.Level, c.Content,
               c.TimesDerived, c.InternalMetadata, c.CreatedAt,
        """;

    private const string FromConclusions = """
         AS Distance
        FROM dbo.Conclusions AS c
        JOIN dbo.Collections AS k ON k.Id = c.CollectionId
        JOIN dbo.Peers AS o ON o.Id = k.ObserverPeerId
        JOIN dbo.Peers AS d ON d.Id = k.ObservedPeerId
        LEFT JOIN dbo.Sessions AS s ON s.Id = c.SessionId
        """;

    /// <summary>The rows of <see cref="ConclusionRow"/>, without a distance; alias <c>c</c> is the conclusion.</summary>
    private const string SelectRows = SelectColumns + " CAST(NULL AS float)" + FromConclusions;

    /// <summary>
    /// The rows with their distance to <c>@vector</c>. It is computed once, by <c>CROSS APPLY v</c>, so it can be filtered
    /// and ordered on; a row without an embedding has a NULL distance, which no bound admits.
    /// </summary>
    private const string SelectMatches = SelectColumns + " v.Distance" + FromConclusions
        + " CROSS APPLY (SELECT VECTOR_DISTANCE('cosine', c.Embedding, @vector) AS Distance) AS v";

    public async Task<CollectionRecord?> GetCollectionAsync(
        string workspaceName, string observerName, string observedName, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);
        var workspaceId = await Lookups.RequireWorkspaceIdAsync(db, workspaceName, ct);
        var row = (await db.Database
                .SqlQueryRaw<CollectionRow>(
                    """
                    SELECT k.NewExplicitCount, k.LastActivityAt, k.LastDreamAt, k.CreatedAt
                    FROM dbo.Collections AS k
                    JOIN dbo.Peers AS o ON o.Id = k.ObserverPeerId
                    JOIN dbo.Peers AS d ON d.Id = k.ObservedPeerId
                    WHERE k.WorkspaceId = @workspace
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
            : new CollectionRecord(workspaceName, observerName, observedName, row.NewExplicitCount, row.LastActivityAt, row.LastDreamAt, row.CreatedAt);
    }

    public async Task<ConclusionRecord?> FindExactAsync(
        string workspaceName,
        string observerName,
        string observedName,
        ConclusionLevel level,
        string content,
        string? restrictToSession,
        CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(content);
        await using var db = await runtime.OpenAsync(ct);
        var workspaceId = await Lookups.RequireWorkspaceIdAsync(db, workspaceName, ct);
        if (await FindScopeAsync(db, workspaceId, observerName, observedName, restrictToSession, ct) is not { } scope)
        {
            return null;
        }

        // Seeks the (CollectionId, Level, ContentHash) index, then confirms the content ordinally.
        var rows = await db.Database
            .SqlQueryRaw<ConclusionRow>(
                string.Concat(
                    "SELECT TOP (1) * FROM (",
                    SelectRows,
                    """
                     WHERE c.CollectionId = @collection AND c.Level = @level AND c.ContentHash = @hash
                       AND c.Content = @content COLLATE Latin1_General_100_BIN2_UTF8 AND c.DeletedAt IS NULL
                       AND (@session IS NULL OR c.SessionId = @session)) AS r
                    ORDER BY r.Id
                    """),
                SqlParameters.Long("@collection", scope.CollectionId),
                SqlParameters.Byte("@level", (byte)level),
                SqlParameters.Binary("@hash", ContentHash.Of(content)),
                SqlParameters.LongText("@content", content),
                SqlParameters.OptionalLong("@session", scope.SessionId))
            .ToListAsync(ct);
        return (await ToRecordsAsync(db, workspaceId, workspaceName, rows, ct)).SingleOrDefault();
    }

    public async Task<ConclusionMatch?> FindNearestAsync(
        string workspaceName,
        string observerName,
        string observedName,
        ConclusionLevel level,
        ReadOnlyMemory<float> vector,
        double maxDistance,
        string? restrictToSession,
        CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        Vectors.Require(vector, nameof(vector));
        await using var db = await runtime.OpenAsync(ct);
        var workspaceId = await Lookups.RequireWorkspaceIdAsync(db, workspaceName, ct);
        if (await FindScopeAsync(db, workspaceId, observerName, observedName, restrictToSession, ct) is not { } scope)
        {
            return null;
        }

        var rows = await db.Database
            .SqlQueryRaw<ConclusionRow>(
                string.Concat(
                    "SELECT TOP (1) * FROM (",
                    SelectMatches,
                    """
                     WHERE c.CollectionId = @collection AND c.Level = @level AND c.EmbeddingState = 1 AND c.DeletedAt IS NULL
                       AND (@session IS NULL OR c.SessionId = @session) AND v.Distance <= @maxDistance) AS r
                    ORDER BY r.Distance, r.CreatedAt, r.Id
                    """),
                SqlParameters.Vector("@vector", vector),
                SqlParameters.Long("@collection", scope.CollectionId),
                SqlParameters.Byte("@level", (byte)level),
                SqlParameters.OptionalLong("@session", scope.SessionId),
                Distance("@maxDistance", maxDistance))
            .ToListAsync(ct);
        return (await ToMatchesAsync(db, workspaceId, workspaceName, rows, ct)).SingleOrDefault();
    }

    public async Task<Page<ConclusionRecord>> ListAsync(
        string workspaceName, FilterNode? filter, ConclusionOrder order, PageRequest page, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(page);
        if (!Enum.IsDefined(order))
        {
            throw new ArgumentOutOfRangeException(nameof(order), order, "Unknown conclusion order.");
        }

        var (where, parameters) = SqlFilterCompiler.Compile(filter, ResourceKind.Conclusion, "c");
        await using var db = await runtime.OpenAsync(ct);
        var workspaceId = await Lookups.RequireWorkspaceIdAsync(db, workspaceName, ct);
        var rows = db.Database.SqlQueryRaw<ConclusionRow>(
            string.Concat(SelectRows, " WHERE c.WorkspaceId = @workspace AND c.DeletedAt IS NULL AND ", where),
            [SqlParameters.Long("@workspace", workspaceId), .. parameters]);
        return await Paging.ToFilteredPageAsync<ConclusionRow, ConclusionRecord>(
            rows,
            page,
            (query, reverse) => (order, reverse) switch
            {
                (ConclusionOrder.CreatedAt, false) => query.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id),
                (ConclusionOrder.CreatedAt, true) => query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id),
                (_, false) => query.OrderBy(r => r.TimesDerived).ThenBy(r => r.CreatedAt).ThenBy(r => r.Id),
                (_, true) => query.OrderByDescending(r => r.TimesDerived).ThenByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id),
            },
            entities => ToRecordsAsync(db, workspaceId, workspaceName, entities, ct),
            ct);
    }

    public async Task<IReadOnlyList<ConclusionMatch>> QueryAsync(
        string workspaceName,
        ReadOnlyMemory<float> vector,
        int topK,
        double? maxDistance,
        FilterNode? filter,
        CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentOutOfRangeException.ThrowIfLessThan(topK, 1);
        Vectors.Require(vector, nameof(vector));
        var (where, parameters) = SqlFilterCompiler.Compile(filter, ResourceKind.Conclusion, "c");
        await using var db = await runtime.OpenAsync(ct);
        var workspaceId = await Lookups.RequireWorkspaceIdAsync(db, workspaceName, ct);

        List<ConclusionRow> rows;
        try
        {
            rows = await db.Database
                .SqlQueryRaw<ConclusionRow>(
                    string.Concat(
                        "SELECT TOP (@topK) * FROM (",
                        SelectMatches,
                        " WHERE c.WorkspaceId = @workspace AND c.EmbeddingState = 1 AND c.DeletedAt IS NULL",
                        maxDistance is null ? string.Empty : " AND v.Distance <= @maxDistance",
                        " AND ",
                        where,
                        ") AS r ORDER BY r.Distance, r.CreatedAt, r.Id"),
                    [
                        SqlParameters.Int("@topK", topK),
                        SqlParameters.Vector("@vector", vector),
                        SqlParameters.Long("@workspace", workspaceId),
                        Distance("@maxDistance", maxDistance ?? 0),
                        .. parameters,
                    ])
                .ToListAsync(ct);
        }
        catch (Exception ex) when (SqlErrors.IsTooComplex(ex))
        {
            throw new NachosValidationException(SqlFilterCompiler.TooComplex, ex);
        }

        return await ToMatchesAsync(db, workspaceId, workspaceName, rows, ct);
    }

    public async Task<ConclusionRecord?> GetAsync(string workspaceName, string publicId, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(publicId);
        await using var db = await runtime.OpenAsync(ct);
        var workspaceId = await Lookups.RequireWorkspaceIdAsync(db, workspaceName, ct);
        var rows = await db.Database
            .SqlQueryRaw<ConclusionRow>(
                string.Concat(
                    SelectRows,
                    " WHERE c.WorkspaceId = @workspace AND c.PublicId = @publicId AND DATALENGTH(c.PublicId) = DATALENGTH(@publicId) AND c.DeletedAt IS NULL"),
                SqlParameters.Long("@workspace", workspaceId),
                SqlParameters.Text("@publicId", publicId))
            .ToListAsync(ct);
        return (await ToRecordsAsync(db, workspaceId, workspaceName, rows, ct)).SingleOrDefault();
    }

    /// <remarks>
    /// One transaction in the lock order of <see cref="SqlWorkQueue"/>: the fence (the lease row, update-locked to commit),
    /// the lease's work items (succeeded), the collections (created for inserts; then the dream counters, in <c>Id</c>
    /// order), the conclusions (inserts; then one update per corroborated or replaced row, in <c>Id</c> order), and last the
    /// lease release. Corroboration and replacement targets are read first, from the snapshot: a target tombstoned after
    /// that read still has its collection's <c>LastActivityAt</c> bumped, but its row is not changed.
    /// </remarks>
    public async Task CommitDerivationAsync(WorkLease lease, DerivationChanges changes, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(changes);
        if (lease.Items.Any(item => !string.Equals(item.WorkspaceName, changes.WorkspaceName, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"The changes are for workspace '{changes.WorkspaceName}', but the lease on '{lease.WorkUnitKey}' holds work of another workspace.",
                nameof(changes));
        }

        var inserts = Own(changes.Inserts, nameof(changes));
        var replacements = changes.Replacements
            .Select(r => new OwnedReplacement(r.ConclusionId, r.Content, Vectors.RequireOptional(r.Embedding, nameof(changes))))
            .ToList();

        await using var db = await runtime.OpenAsync(ct);
        var now = runtime.Clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var workspaceId = await Lookups.RequireWorkspaceIdAsync(db, changes.WorkspaceName, ct);
        await SqlWorkQueue.RequireHeldAsync(db, lease, ct);
        var targets = await ResolveTargetsAsync(db, workspaceId, changes.Corroborations, replacements, ct);

        await SqlWorkQueue.CompleteItemsAsync(db, lease, now, ct);

        var resolved = await ResolveReferencesAsync(db, workspaceId, changes.WorkspaceName, inserts, now, ct);
        var counters = new SortedDictionary<long, int>();
        foreach (var (conclusion, reference) in inserts.Zip(resolved))
        {
            counters[reference.CollectionId] = counters.GetValueOrDefault(reference.CollectionId)
                + (conclusion.Source.Level == ConclusionLevel.Explicit ? 1 : 0);
        }

        foreach (var target in targets)
        {
            counters.TryAdd(target.CollectionId, 0);
        }

        await UpdateCountersAsync(db, counters, now, ct);
        await InsertRowsAsync(db, workspaceId, inserts, resolved, now, ct);
        await RederiveAsync(db, targets, ct);
        await SqlWorkQueue.ReleaseAsync(db, lease, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ConclusionRecord>> InsertAsync(
        string workspaceName, IReadOnlyList<NewConclusion> conclusions, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        var owned = Own(conclusions, nameof(conclusions));
        await using var db = await runtime.OpenAsync(ct);
        var now = runtime.Clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var workspaceId = await Lookups.RequireWorkspaceIdAsync(db, workspaceName, ct);
        var resolved = await ResolveReferencesAsync(db, workspaceId, workspaceName, owned, now, ct);
        var inserted = await InsertRowsAsync(db, workspaceId, owned, resolved, now, ct);
        await transaction.CommitAsync(ct);

        return
        [
            .. owned.Zip(inserted).Select(pair => new ConclusionRecord(
                pair.Second.PublicId,
                workspaceName,
                pair.First.Source.ObserverName,
                pair.First.Source.ObservedName,
                pair.First.Source.SessionName,
                pair.First.Source.Level,
                pair.First.Source.Content,
                TimesDerived: 1,
                pair.Second.SourceIds,
                SqlJson.FromStorage(pair.First.InternalMetadata),
                now)),
        ];
    }

    public async Task TombstoneAsync(string workspaceName, string publicId, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(publicId);
        await using var db = await runtime.OpenAsync(ct);
        var workspaceId = await Lookups.RequireWorkspaceIdAsync(db, workspaceName, ct);
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE dbo.Conclusions SET DeletedAt = @now
            WHERE WorkspaceId = @workspace AND PublicId = @publicId AND DATALENGTH(PublicId) = DATALENGTH(@publicId) AND DeletedAt IS NULL
            """,
            [
                SqlParameters.Time("@now", runtime.Clock.GetUtcNow()),
                SqlParameters.Long("@workspace", workspaceId),
                SqlParameters.Text("@publicId", publicId),
            ],
            ct);
    }

    /// <remarks>Deletes the purged conclusions' own source rows first; rows naming them as a source stay (no FK).</remarks>
    public async Task<int> PurgeTombstonesAsync(TimeSpan retention, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        var cutoff = Clamp.Subtract(runtime.Clock.GetUtcNow(), retention);
        await using var db = await runtime.OpenAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE cs FROM dbo.ConclusionSources AS cs JOIN dbo.Conclusions AS c ON c.Id = cs.DerivedId WHERE c.DeletedAt <= @cutoff",
            [SqlParameters.Time("@cutoff", cutoff)],
            ct);
        var purged = await db.Database.ExecuteSqlRawAsync(
            "DELETE dbo.Conclusions WHERE DeletedAt <= @cutoff",
            [SqlParameters.Time("@cutoff", cutoff)],
            ct);
        await transaction.CommitAsync(ct);
        return purged;
    }

    /// <summary>A distance bound; the cosine distance is a <c>float</c>.</summary>
    private static SqlParameter Distance(string name, double value) => new(name, SqlDbType.Float) { Value = value };

    /// <summary>Validated, storable copies of the caller's conclusions; no round trip.</summary>
    private static List<OwnedConclusion> Own(IReadOnlyList<NewConclusion> conclusions, string paramName)
    {
        ArgumentNullException.ThrowIfNull(conclusions);
        return
        [
            .. conclusions.Select(c => new OwnedConclusion(
                c,
                SqlJson.ToStorage(c.InternalMetadata, JsonField.InternalMetadata),
                Vectors.RequireOptional(c.Embedding, paramName))),
        ];
    }

    /// <summary>
    /// The collection and the session a dedup lookup is scoped to; null when the collection was never written or the
    /// session does not exist, so nothing can match.
    /// </summary>
    private static async Task<(long CollectionId, long? SessionId)?> FindScopeAsync(
        NachosDbContext db, long workspaceId, string observerName, string observedName, string? sessionName, CancellationToken ct)
    {
        var observer = await FindPeerIdAsync(db, workspaceId, observerName, ct);
        var observed = await FindPeerIdAsync(db, workspaceId, observedName, ct);
        if (observer is null || observed is null)
        {
            return null;
        }

        var collection = await FindCollectionIdAsync(db, workspaceId, observer.Value, observed.Value, ct);
        if (collection is null)
        {
            return null;
        }

        if (sessionName is null)
        {
            return (collection.Value, null);
        }

        var session = await db.Sessions.Named(workspaceId, sessionName).Select(s => (long?)s.Id).FirstOrDefaultAsync(ct);
        return session is null ? null : (collection.Value, session);
    }

    private static Task<long?> FindPeerIdAsync(NachosDbContext db, long workspaceId, string name, CancellationToken ct) =>
        db.Peers.Named(workspaceId, name).Select(p => (long?)p.Id).FirstOrDefaultAsync(ct);

    private static async Task<long?> FindCollectionIdAsync(NachosDbContext db, long workspaceId, long observer, long observed, CancellationToken ct) =>
        (await db.Database
            .SqlQueryRaw<long>(
                "SELECT Id AS [Value] FROM dbo.Collections WHERE WorkspaceId = @workspace AND ObserverPeerId = @observer AND ObservedPeerId = @observed",
                SqlParameters.Long("@workspace", workspaceId),
                SqlParameters.Long("@observer", observer),
                SqlParameters.Long("@observed", observed))
            .ToListAsync(ct))
        .Cast<long?>()
        .SingleOrDefault();

    /// <summary>
    /// The collection and session of each conclusion to insert, creating missing collections (get-or-create: a unique
    /// violation means a concurrent writer created it, which is then read). Peers are looked up in ordinal name order.
    /// </summary>
    /// <exception cref="NotFoundException">A conclusion names a peer or session that does not exist.</exception>
    private static async Task<List<References>> ResolveReferencesAsync(
        NachosDbContext db, long workspaceId, string workspaceName, List<OwnedConclusion> conclusions, DateTimeOffset now, CancellationToken ct)
    {
        var peers = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in conclusions
                     .SelectMany(c => new[] { c.Source.ObserverName, c.Source.ObservedName })
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            peers[name] = await FindPeerIdAsync(db, workspaceId, name, ct) ?? throw Lookups.PeerNotFound(workspaceName, name);
        }

        var sessions = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in conclusions.Select(c => c.Source.SessionName).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            sessions[name] = await db.Sessions.Named(workspaceId, name).Select(s => (long?)s.Id).FirstOrDefaultAsync(ct)
                ?? throw Lookups.SessionNotFound(workspaceName, name);
        }

        var collections = new Dictionary<(long, long), long>();
        var references = new List<References>(conclusions.Count);
        foreach (var conclusion in conclusions)
        {
            var key = (peers[conclusion.Source.ObserverName], peers[conclusion.Source.ObservedName]);
            if (!collections.TryGetValue(key, out var collectionId))
            {
                collectionId = collections[key] = await EnsureCollectionAsync(db, workspaceId, key.Item1, key.Item2, now, ct);
            }

            references.Add(new References(collectionId, conclusion.Source.SessionName is { } session ? sessions[session] : null));
        }

        return references;
    }

    private static async Task<long> EnsureCollectionAsync(
        NachosDbContext db, long workspaceId, long observer, long observed, DateTimeOffset now, CancellationToken ct)
    {
        if (await FindCollectionIdAsync(db, workspaceId, observer, observed, ct) is { } existing)
        {
            return existing;
        }

        try
        {
            return (await db.Database
                    .SqlQueryRaw<long>(
                        """
                        INSERT dbo.Collections (WorkspaceId, ObserverPeerId, ObservedPeerId, CreatedAt)
                        OUTPUT inserted.Id AS [Value]
                        VALUES (@workspace, @observer, @observed, @now)
                        """,
                        SqlParameters.Long("@workspace", workspaceId),
                        SqlParameters.Long("@observer", observer),
                        SqlParameters.Long("@observed", observed),
                        SqlParameters.Time("@now", now))
                    .ToListAsync(ct))
                .Single();
        }
        catch (Exception ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            return await FindCollectionIdAsync(db, workspaceId, observer, observed, ct)
                ?? throw new InvalidOperationException("A collection's insert lost a race to a row it cannot read.", ex);
        }
    }

    /// <summary>
    /// Inserts the conclusions (pending unless an embedding is given) and their source rows, and returns each one's public
    /// id and stored source ids. A source id that names no conclusion of the workspace cannot be stored and is left out.
    /// </summary>
    private static async Task<List<(string PublicId, IReadOnlyList<string> SourceIds)>> InsertRowsAsync(
        NachosDbContext db, long workspaceId, List<OwnedConclusion> conclusions, List<References> references, DateTimeOffset now, CancellationToken ct)
    {
        var sources = await ResolveSourcesAsync(db, workspaceId, conclusions.SelectMany(c => c.Source.SourceIds), ct);
        var inserted = new List<(string, IReadOnlyList<string>)>(conclusions.Count);
        var publicIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (conclusion, reference) in conclusions.Zip(references))
        {
            string publicId;
            do
            {
                publicId = PublicId.New();
            }
            while (!publicIds.Add(publicId));

            var embedding = conclusion.Embedding;
            var parameters = new List<SqlParameter>
            {
                SqlParameters.Long("@workspace", workspaceId),
                SqlParameters.Text("@publicId", publicId),
                SqlParameters.Long("@collection", reference.CollectionId),
                SqlParameters.OptionalLong("@session", reference.SessionId),
                SqlParameters.Byte("@level", (byte)conclusion.Source.Level),
                SqlParameters.LongText("@content", conclusion.Source.Content),
                SqlParameters.Binary("@hash", ContentHash.Of(conclusion.Source.Content)),
                SqlParameters.Byte("@state", embedding is null ? (byte)0 : (byte)1),
                SqlParameters.LongText("@metadata", conclusion.InternalMetadata),
                SqlParameters.Time("@now", now),
            };
            if (embedding is { } vector)
            {
                parameters.Add(SqlParameters.Vector("@embedding", vector));
            }

            var sql = embedding is null
                ? """
                  INSERT dbo.Conclusions (WorkspaceId, PublicId, CollectionId, SessionId, Level, Content, ContentHash, EmbeddingState, InternalMetadata, CreatedAt)
                  OUTPUT inserted.Id AS [Value]
                  VALUES (@workspace, @publicId, @collection, @session, @level, @content, @hash, @state, @metadata, @now)
                  """
                : """
                  INSERT dbo.Conclusions (WorkspaceId, PublicId, CollectionId, SessionId, Level, Content, ContentHash, Embedding, EmbeddingState, InternalMetadata, CreatedAt)
                  OUTPUT inserted.Id AS [Value]
                  VALUES (@workspace, @publicId, @collection, @session, @level, @content, @hash, @embedding, @state, @metadata, @now)
                  """;
            var id = (await db.Database.SqlQueryRaw<long>(sql, [.. parameters]).ToListAsync(ct)).Single();

            var sourceIds = conclusion.Source.SourceIds.Where(sources.ContainsKey).ToList();
            if (sourceIds.Count > 0)
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT dbo.ConclusionSources (DerivedId, Position, SourceId)
                    SELECT @derived, CAST([key] AS int), CAST([value] AS bigint) FROM OPENJSON(@sources)
                    """,
                    [
                        SqlParameters.Long("@derived", id),
                        SqlParameters.LongText("@sources", JsonSerializer.Serialize(sourceIds.Select(source => sources[source]))),
                    ],
                    ct);
            }

            inserted.Add((publicId, sourceIds));
        }

        return inserted;
    }

    /// <summary>The internal ids of the workspace's conclusions (live or tombstoned) with these public ids.</summary>
    private static async Task<Dictionary<string, long>> ResolveSourcesAsync(
        NachosDbContext db, long workspaceId, IEnumerable<string> publicIds, CancellationToken ct)
    {
        var distinct = publicIds.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }

        var rows = await db.Database
            .SqlQueryRaw<SourceRow>(
                """
                SELECT c.Id, c.PublicId FROM dbo.Conclusions AS c
                JOIN OPENJSON(@ids) AS j ON c.PublicId = j.[value] COLLATE Latin1_General_100_BIN2_UTF8 AND DATALENGTH(c.PublicId) = DATALENGTH(j.[value])
                WHERE c.WorkspaceId = @workspace
                """,
                SqlParameters.LongText("@ids", JsonSerializer.Serialize(distinct)),
                SqlParameters.Long("@workspace", workspaceId))
            .ToListAsync(ct);
        return rows.ToDictionary(row => row.PublicId, row => row.Id, StringComparer.Ordinal);
    }

    /// <summary>
    /// The live conclusions a derivation corroborates or replaces, in <c>Id</c> order, each with how much it is re-derived
    /// (each corroboration counts; a replacement counts once unless the id is also corroborated) and its last replacement.
    /// Missing and tombstoned ids are left out.
    /// </summary>
    private static async Task<List<Target>> ResolveTargetsAsync(
        NachosDbContext db,
        long workspaceId,
        IReadOnlyList<string> corroborations,
        List<OwnedReplacement> replacements,
        CancellationToken ct)
    {
        var ids = corroborations.Concat(replacements.Select(r => r.ConclusionId)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var live = await db.Database
            .SqlQueryRaw<TargetRow>(
                """
                SELECT c.Id, c.PublicId, c.CollectionId FROM dbo.Conclusions AS c
                JOIN OPENJSON(@ids) AS j ON c.PublicId = j.[value] COLLATE Latin1_General_100_BIN2_UTF8 AND DATALENGTH(c.PublicId) = DATALENGTH(j.[value])
                WHERE c.WorkspaceId = @workspace AND c.DeletedAt IS NULL
                """,
                SqlParameters.LongText("@ids", JsonSerializer.Serialize(ids)),
                SqlParameters.Long("@workspace", workspaceId))
            .ToListAsync(ct);

        var corroborated = corroborations.CountBy(id => id, StringComparer.Ordinal).ToDictionary(StringComparer.Ordinal);
        return
        [
            .. live.OrderBy(row => row.Id).Select(row =>
            {
                var replacementCount = replacements.Count(r => string.Equals(r.ConclusionId, row.PublicId, StringComparison.Ordinal));
                var increment = corroborated.TryGetValue(row.PublicId, out var count) ? count : replacementCount;
                var replacement = replacements.LastOrDefault(r => string.Equals(r.ConclusionId, row.PublicId, StringComparison.Ordinal));
                return new Target(row.Id, row.CollectionId, increment, replacement);
            }),
        ];
    }

    /// <summary>Adds explicit inserts to <c>NewExplicitCount</c> and sets <c>LastActivityAt</c>, one collection at a time in <c>Id</c> order.</summary>
    private static async Task UpdateCountersAsync(NachosDbContext db, SortedDictionary<long, int> counters, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (collectionId, explicitInserts) in counters)
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE dbo.Collections SET NewExplicitCount = NewExplicitCount + @explicit, LastActivityAt = @now WHERE Id = @collection",
                [SqlParameters.Int("@explicit", explicitInserts), SqlParameters.Time("@now", now), SqlParameters.Long("@collection", collectionId)],
                ct);
        }
    }

    /// <summary>Applies corroborations and replacements, one row at a time in <c>Id</c> order; a row tombstoned meanwhile is left alone.</summary>
    private static async Task RederiveAsync(NachosDbContext db, List<Target> targets, CancellationToken ct)
    {
        foreach (var target in targets)
        {
            var parameters = new List<SqlParameter>
            {
                SqlParameters.Int("@increment", target.Increment),
                SqlParameters.Long("@id", target.Id),
            };
            var assignments = "TimesDerived = TimesDerived + @increment";
            if (target.Replacement is { } replacement)
            {
                parameters.Add(SqlParameters.LongText("@content", replacement.Content));
                parameters.Add(SqlParameters.Binary("@hash", ContentHash.Of(replacement.Content)));
                if (replacement.Embedding is { } vector)
                {
                    parameters.Add(SqlParameters.Vector("@embedding", vector));
                    assignments += ", Content = @content, ContentHash = @hash, Embedding = @embedding, EmbeddingState = 1";
                }
                else
                {
                    assignments += ", Content = @content, ContentHash = @hash, Embedding = NULL, EmbeddingState = 0";
                }
            }

            var sql = string.Concat("UPDATE dbo.Conclusions SET ", assignments, " WHERE Id = @id AND DeletedAt IS NULL");
            await db.Database.ExecuteSqlRawAsync(sql, parameters, ct);
        }
    }

    private static async Task<IReadOnlyList<ConclusionRecord>> ToRecordsAsync(
        NachosDbContext db, long workspaceId, string workspaceName, List<ConclusionRow> rows, CancellationToken ct)
    {
        var sources = await SourceIdsAsync(db, workspaceId, rows, ct);
        return
        [
            .. rows.Select(row => new ConclusionRecord(
                row.PublicId,
                workspaceName,
                row.ObserverName,
                row.ObservedName,
                row.SessionName,
                (ConclusionLevel)row.Level,
                row.Content,
                row.TimesDerived,
                sources.TryGetValue(row.Id, out var ids) ? ids : [],
                SqlJson.FromStorage(row.InternalMetadata),
                row.CreatedAt)),
        ];
    }

    private static async Task<IReadOnlyList<ConclusionMatch>> ToMatchesAsync(
        NachosDbContext db, long workspaceId, string workspaceName, List<ConclusionRow> rows, CancellationToken ct)
    {
        var records = await ToRecordsAsync(db, workspaceId, workspaceName, rows, ct);
        return [.. records.Zip(rows).Select(pair => new ConclusionMatch(pair.First, pair.Second.Distance!.Value))];
    }

    /// <summary>The source public ids of each row that has any, in <c>Position</c> order, leaving out purged sources.</summary>
    private static async Task<Dictionary<long, IReadOnlyList<string>>> SourceIdsAsync(
        NachosDbContext db, long workspaceId, List<ConclusionRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var links = await db.Database
            .SqlQueryRaw<SourceLinkRow>(
                """
                SELECT cs.DerivedId, cs.Position, src.PublicId
                FROM dbo.ConclusionSources AS cs
                JOIN dbo.Conclusions AS src ON src.Id = cs.SourceId AND src.WorkspaceId = @workspace
                WHERE cs.DerivedId IN (SELECT CAST([value] AS bigint) FROM OPENJSON(@ids))
                """,
                SqlParameters.Long("@workspace", workspaceId),
                SqlParameters.LongText("@ids", JsonSerializer.Serialize(rows.Select(row => row.Id))))
            .ToListAsync(ct);
        return links
            .GroupBy(link => link.DerivedId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)[.. group.OrderBy(link => link.Position).Select(link => link.PublicId)]);
    }

    /// <summary>A validated conclusion to insert, with its internal metadata as the stored JSON text.</summary>
    private sealed record OwnedConclusion(NewConclusion Source, string InternalMetadata, ReadOnlyMemory<float>? Embedding);

    private sealed record OwnedReplacement(string ConclusionId, string Content, ReadOnlyMemory<float>? Embedding);

    private sealed record References(long CollectionId, long? SessionId);

    private sealed record Target(long Id, long CollectionId, int Increment, OwnedReplacement? Replacement);

    private sealed record CollectionRow(int NewExplicitCount, DateTimeOffset? LastActivityAt, DateTimeOffset? LastDreamAt, DateTimeOffset CreatedAt);

    private sealed record TargetRow(long Id, string PublicId, long CollectionId);

    private sealed record SourceRow(long Id, string PublicId);

    private sealed record SourceLinkRow(long DerivedId, int Position, string PublicId);
}
