using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.SqlServer.Filtering;
using Nachos.DataLayer.SqlServer.Search;
using Nachos.DataLayer.SqlServer.Storage;

namespace Nachos.DataLayer.SqlServer.Stores;

/// <remarks>
/// <para>
/// <b>Candidates</b> (alias <c>m</c>): the workspace's messages, narrowed by the scope (one session, or the sessions and
/// <c>JoinedAt &lt;= CreatedAt &lt; LeftAt</c> windows of the peer's <c>SessionPeers</c> rows), then by the message filter
/// (<see cref="SqlFilterCompiler"/>). A workspace or session being deleted (<c>LifecycleState = 2</c>) has none.
/// </para>
/// <para>
/// <b>Vector</b> search is exact: <c>VECTOR_DISTANCE('cosine', …)</c> over the synced chunks of the candidates, each
/// message ranked by its nearest chunk.
/// </para>
/// <para>
/// <b>Lexical</b> search uses <c>CONTAINSTABLE</c> while <see cref="FullTextIndexState.IsReady"/> and
/// <see cref="FullTextCondition.For"/> gives a condition (every term a plain word): any term as a word or word prefix finds
/// a candidate, and one <c>CONTAINS</c> per term counts the terms it matched. Otherwise it is the contract's own
/// definition: a term matches as a substring, compared <c>UPPER</c>-folded under a binary collation (as <c>icontains</c>
/// is), so a term inside a word, or in unspaced (for example CJK) text, matches only on this fallback. Both rank by the
/// number of terms matched, never by full-text <c>RANK</c>, so they order alike and as the in-memory provider does.
/// </para>
/// <para>Ties go to the newest message: <c>CreatedAt</c>, then <c>Seq</c>, descending, then public id.</para>
/// </remarks>
internal sealed class SqlMessageSearch(SqlStoreRuntime runtime, FullTextIndexState fullTextIndex) : IMessageSearch
{
    private const string Bin2 = "Latin1_General_100_BIN2";

    private const string Folded = $"UPPER(m.Content COLLATE {Bin2})";

    private const string SelectHits = """
        SELECT TOP (@take) m.PublicId, s.Name AS SessionName, p.Name AS PeerName, m.Seq, m.Content, m.TokenCount, m.Metadata, m.CreatedAt
        """;

    private const string HitsFrom = """
        JOIN dbo.Sessions AS s ON s.Id = m.SessionId
        JOIN dbo.Peers AS p ON p.Id = m.PeerId
        """;

    private const string NewestFirst = "m.CreatedAt DESC, m.Seq DESC, m.PublicId";

    public async Task<IReadOnlyList<MessageHit>> VectorAsync(
        MessageSearchRequest request, ReadOnlyMemory<float> query, int take, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        Vectors.Require(query, nameof(query));
        return await SearchAsync(
            request,
            take,
            candidates => (
                Lines(
                    SelectHits,
                    """
                    FROM (
                        SELECT e.MessageId, MIN(VECTOR_DISTANCE('cosine', e.Embedding, @query)) AS Distance
                        FROM dbo.MessageEmbeddings AS e
                        JOIN dbo.Messages AS m ON m.Id = e.MessageId
                        JOIN dbo.Sessions AS s ON s.Id = m.SessionId
                        WHERE e.WorkspaceId = @workspace AND e.SyncState = 1
                    """,
                    "AND " + candidates,
                    """
                        GROUP BY e.MessageId
                    ) AS d
                    JOIN dbo.Messages AS m ON m.Id = d.MessageId
                    """,
                    HitsFrom,
                    "ORDER BY d.Distance, " + NewestFirst),
                [SqlParameters.Vector("@query", query)]),
            ct);
    }

    public async Task<IReadOnlyList<MessageHit>> LexicalAsync(
        MessageSearchRequest request, IReadOnlyList<string> terms, int take, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        if (terms.Any(term => string.IsNullOrEmpty(term) || term.Length > IMessageSearch.MaxTermLength))
        {
            throw new ArgumentException(
                $"A search term must be a non-empty word of at most {IMessageSearch.MaxTermLength} characters.", nameof(terms));
        }

        // Terms equal but for case are one term, so each counts once towards the coverage.
        string[] distinct = [.. terms.Distinct(StringComparer.OrdinalIgnoreCase)];
        var fullText = fullTextIndex.IsReady ? FullTextCondition.For(distinct) : null;
        return await SearchAsync(
            request,
            take,
            candidates => distinct.Length == 0 ? null
                : fullText is not null ? FullTextQuery(candidates, fullText, distinct)
                : LikeQuery(candidates, distinct),
            ct);
    }

    private static (string Sql, SqlParameter[] Parameters) FullTextQuery(string candidates, string condition, string[] terms)
    {
        // Sized, never nvarchar(max), which CONTAINSTABLE and CONTAINS refuse; FullTextCondition keeps each within it.
        var parameters = new List<SqlParameter>(terms.Length + 1) { FullTextParameter("@condition", condition) };
        var matched = new List<string>(terms.Length);
        for (var i = 0; i < terms.Length; i++)
        {
            matched.Add($"CASE WHEN CONTAINS(m.Content, @term{i}) THEN 1 ELSE 0 END");
            parameters.Add(FullTextParameter($"@term{i}", FullTextCondition.Term(terms[i])));
        }

        return (
            Lines(
                SelectHits,
                """
                FROM CONTAINSTABLE(dbo.Messages, Content, @condition) AS ft
                JOIN dbo.Messages AS m ON m.Id = ft.[KEY]
                """,
                HitsFrom,
                "WHERE " + candidates,
                $"ORDER BY {string.Join(" + ", matched)} DESC, {NewestFirst}"),
            [.. parameters]);
    }

    private static SqlParameter FullTextParameter(string name, string condition) =>
        new(name, SqlDbType.NVarChar, FullTextCondition.MaxLength) { Value = condition };

    private static (string Sql, SqlParameter[] Parameters) LikeQuery(string candidates, string[] terms)
    {
        var matched = new List<string>(terms.Length);
        var parameters = new List<SqlParameter>(terms.Length);
        for (var i = 0; i < terms.Length; i++)
        {
            matched.Add($"CASE WHEN {Folded} LIKE UPPER(@pattern{i} COLLATE {Bin2}) ESCAPE N'{LikePattern.Escape}' THEN 1 ELSE 0 END");
            parameters.Add(SqlParameters.Text($"@pattern{i}", LikePattern.For(terms[i])));
        }

        return (
            Lines(
                SelectHits,
                "FROM dbo.Messages AS m",
                HitsFrom,
                "CROSS APPLY (SELECT " + string.Join(" + ", matched) + " AS Matched) AS c",
                "WHERE " + candidates,
                "AND c.Matched > 0",
                "ORDER BY c.Matched DESC, " + NewestFirst),
            [.. parameters]);
    }

    /// <summary>SQL fragments, one per line: a raw string literal does not end with a line break.</summary>
    private static string Lines(params string[] fragments) => string.Join('\n', fragments);

    /// <summary>
    /// Runs the query <paramref name="build"/> makes from the candidate predicate over <c>m</c> and <c>s</c>; null runs nothing.
    /// </summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    private async Task<IReadOnlyList<MessageHit>> SearchAsync(
        MessageSearchRequest request,
        int take,
        Func<string, (string Sql, SqlParameter[] Parameters)?> build,
        CancellationToken ct)
    {
        var (filter, filterParameters) = SqlFilterCompiler.Compile(request.Filter, ResourceKind.Message, "m");
        await using var db = await runtime.OpenAsync(ct);
        var workspace = await db.Workspaces.Named(request.WorkspaceName)
            .Select(w => new { w.Id, w.LifecycleState })
            .FirstOrDefaultAsync(ct)
            ?? throw Lookups.WorkspaceNotFound(request.WorkspaceName);
        if (workspace.LifecycleState == (byte)LifecycleState.Deleting)
        {
            return [];
        }

        var (scope, scopeParameters) = Scope(request.Scope);
        var candidates = string.Concat("m.WorkspaceId = @workspace AND s.LifecycleState <> 2 AND ", scope, " AND ", filter);
        if (build(candidates) is not { } query)
        {
            return [];
        }

        List<HitRow> rows;
        try
        {
            rows = await db.Database
                .SqlQueryRaw<HitRow>(
                    query.Sql,
                    [
                        SqlParameters.Int("@take", take),
                        SqlParameters.Long("@workspace", workspace.Id),
                        .. scopeParameters,
                        .. filterParameters,
                        .. query.Parameters,
                    ])
                .ToListAsync(ct);
        }
        catch (Exception ex) when (SqlErrors.IsTooComplex(ex))
        {
            throw new NachosValidationException(SqlFilterCompiler.TooComplex, ex);
        }

        return
        [
            .. rows.Select((row, index) => new MessageHit(
                new MessageRecord(
                    row.PublicId,
                    request.WorkspaceName,
                    row.SessionName,
                    row.PeerName,
                    row.Seq,
                    row.Content,
                    row.TokenCount,
                    SqlJson.FromStorage(row.Metadata),
                    row.CreatedAt),
                index + 1)),
        ];
    }

    /// <summary>The scope as a predicate over <c>m</c> and <c>s</c>; a session or peer that does not exist matches nothing.</summary>
    private static (string Sql, SqlParameter[] Parameters) Scope(MessageSearchScope scope) => scope switch
    {
        MessageSearchScope.Workspace => ("1 = 1", []),
        MessageSearchScope.Session { SessionName: var name } => (
            "s.Name = @scopeSession AND DATALENGTH(s.Name) = DATALENGTH(@scopeSession)",
            [SqlParameters.Text("@scopeSession", name)]),
        MessageSearchScope.PeerPerspective { PeerName: var name } => (
            """
            EXISTS (
                SELECT 1
                FROM dbo.SessionPeers AS sp
                JOIN dbo.Peers AS member ON member.Id = sp.PeerId
                WHERE sp.WorkspaceId = m.WorkspaceId AND sp.SessionId = m.SessionId
                  AND member.WorkspaceId = m.WorkspaceId
                  AND member.Name = @scopePeer AND DATALENGTH(member.Name) = DATALENGTH(@scopePeer)
                  AND sp.JoinedAt <= m.CreatedAt AND (sp.LeftAt IS NULL OR m.CreatedAt < sp.LeftAt))
            """,
            [SqlParameters.Text("@scopePeer", name)]),
        null => throw new ArgumentNullException(nameof(scope)),
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown message search scope."),
    };

    private sealed record HitRow(
        string PublicId,
        string SessionName,
        string PeerName,
        long Seq,
        string Content,
        int TokenCount,
        string Metadata,
        DateTimeOffset CreatedAt);
}
