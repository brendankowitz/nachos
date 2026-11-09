using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Work;
using Nachos.DataLayer.SqlServer.Search;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

[Collection(SqlServerDockerGroup.Name)]
public sealed class FullTextIndexMaintenanceTests(SqlServerFixture fixture)
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(2);

    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task CreatesIndexOnce_UnderTwoReplicas()
    {
        var database = await SqlTestDatabase.CreateAsync(fixture);
        database.Options.FullTextIndexPollInterval = TimeSpan.FromMilliseconds(100);
        var loggers = new[] { new CollectingLogger(), new CollectingLogger() };
        var states = new[] { new FullTextIndexState(), new FullTextIndexState() };
        var replicas = new[]
        {
            database.CreateFullTextIndexMaintenance(states[0], TimeProvider.System, loggers[0]),
            database.CreateFullTextIndexMaintenance(states[1], TimeProvider.System, loggers[1]),
        };

        // The hosted loops, started together, race for the lease on the first check.
        await Task.WhenAll(replicas.Select(replica => replica.StartAsync(Ct)));
        try
        {
            await WaitUntilAsync(() => states.All(state => state.IsReady));
        }
        finally
        {
            await Task.WhenAll(replicas.Select(replica => replica.StopAsync(Ct)));
        }

        (await ScalarAsync<int>(database, $"SELECT COUNT(*) FROM sys.fulltext_catalogs WHERE name = N'{FullTextIndexMaintenance.CatalogName}'")).ShouldBe(1);
        (await ScalarAsync<int>(database, "SELECT COUNT(*) FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'dbo.Messages') AND stoplist_id IS NULL")).ShouldBe(1);
        loggers.ShouldAllBe(logger => logger.Warnings.Count == 0 && logger.Errors.Count == 0);

        // The DDL ran under the lease, which was released (a released lease expires at the minimum time).
        (await ScalarAsync<DateTimeOffset>(
                database,
                $"SELECT ExpiresAt FROM dbo.WorkUnitLeases WHERE WorkUnitKey = N'{WorkUnitKeys.FullTextMaintenance}'"))
            .ShouldBe(DateTimeOffset.MinValue);

        // A later check, with the index in place, changes nothing and keeps it ready.
        await replicas[0].CheckAsync(Ct);
        states[0].IsReady.ShouldBeTrue();
    }

    [Fact]
    public async Task IsReady_OnlyAfterPopulation()
    {
        var database = await SqlTestDatabase.CreateAsync(fixture);
        const int Messages = 50_000;
        await SeedMessagesAsync(database, Messages);
        var state = new FullTextIndexState();
        var maintenance = database.CreateFullTextIndexMaintenance(state, TimeProvider.System);
        state.IsReady.ShouldBeFalse();

        // The check that creates the index finds its first population running.
        await maintenance.CheckAsync(Ct);
        (await ScalarAsync<int>(database, "SELECT COUNT(*) FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'dbo.Messages')")).ShouldBe(1);
        state.IsReady.ShouldBeFalse();

        await WaitUntilReadyAsync(maintenance, state);

        // Ready means populated: every message is in the index.
        (await ScalarAsync<int>(database, "SELECT COUNT(*) FROM CONTAINSTABLE(dbo.Messages, Content, N'\"zebra\"')")).ShouldBe(Messages);
    }

    [Fact]
    public async Task StaysReady_WhileTrackedChangesAreApplied()
    {
        var database = await SqlTestDatabase.CreateAsync(fixture);
        await SeedMessagesAsync(database, 1_000);
        var state = new FullTextIndexState();
        var maintenance = database.CreateFullTextIndexMaintenance(state, TimeProvider.System);
        await WaitUntilReadyAsync(maintenance, state);

        // Each batch of writes is applied to the index by change tracking (populate status 3 or 4), which is not a population.
        for (var batch = 0; batch < 10; batch++)
        {
            await InsertMessagesAsync(database, 1_000 + (batch * 5_000), 5_000);
            await maintenance.CheckAsync(Ct);
            state.IsReady.ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData(true, true, "F", 0, true)]
    [InlineData(true, true, "U", 3, true)]
    [InlineData(true, true, "U", 4, true)]
    [InlineData(true, false, "U", 4, true)] // change tracking applying writes: has_crawl_completed reads 0
    [InlineData(true, false, "U", 0, true)]
    [InlineData(true, false, "F", 1, false)] // first population, or after a stoplist change
    [InlineData(true, false, "F", 0, false)]
    [InlineData(true, false, "I", 2, false)]
    [InlineData(true, false, "P", 5, false)]
    [InlineData(true, true, "F", 1, false)]
    [InlineData(true, true, "F", 2, false)]
    [InlineData(true, true, "F", 5, false)]
    [InlineData(true, true, "F", 6, false)]
    [InlineData(true, true, "F", null, false)]
    [InlineData(false, true, "F", 0, false)]
    public void Readiness_FollowsEnabledCrawlAndPopulateStatus(bool enabled, bool crawlCompleted, string crawlType, int? populateStatus, bool ready) =>
        FullTextIndexMaintenance.IsPopulated(enabled, crawlCompleted, crawlType, populateStatus).ShouldBe(ready);

    [Fact]
    public async Task ExistingIndexWithStoplist_IsTurnedOff()
    {
        // An index as the first M3 build created it: the system stoplist, which drops words such as "the".
        var database = await SqlTestDatabase.CreateAsync(fixture);
        await SeedMessagesAsync(database, 10);
        await ExecuteAsync(database, $"CREATE FULLTEXT CATALOG [{FullTextIndexMaintenance.CatalogName}]");
        await ExecuteAsync(
            database,
            $"CREATE FULLTEXT INDEX ON dbo.Messages (Content) KEY INDEX PK_Messages ON [{FullTextIndexMaintenance.CatalogName}] WITH CHANGE_TRACKING AUTO");
        var state = new FullTextIndexState();
        var maintenance = database.CreateFullTextIndexMaintenance(state, TimeProvider.System);

        await WaitUntilReadyAsync(maintenance, state);

        (await ScalarAsync<int>(database, "SELECT COUNT(*) FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'dbo.Messages') AND stoplist_id IS NULL")).ShouldBe(1);
        (await ScalarAsync<int>(database, "SELECT COUNT(*) FROM CONTAINSTABLE(dbo.Messages, Content, N'\"the*\"')")).ShouldBe(1);
    }

    [Fact]
    public async Task UnusableKey_IsReportedAndNoIndexIsCreated()
    {
        var database = await SqlTestDatabase.CreateAsync(fixture);
        // A two-column PK_Messages: not a key full-text search accepts.
        await ExecuteAsync(
            database,
            """
            DECLARE @sql nvarchar(max) = N'';
            SELECT @sql += N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(parent_object_id)) + N' DROP CONSTRAINT ' + QUOTENAME(name) + N';'
            FROM sys.foreign_keys WHERE referenced_object_id = OBJECT_ID(N'dbo.Messages');
            EXEC (@sql);
            ALTER TABLE dbo.Messages DROP CONSTRAINT PK_Messages;
            ALTER TABLE dbo.Messages ADD CONSTRAINT PK_Messages PRIMARY KEY NONCLUSTERED (Id, WorkspaceId);
            """);
        var logger = new CollectingLogger();
        var state = new FullTextIndexState();
        var maintenance = database.CreateFullTextIndexMaintenance(state, TimeProvider.System, logger);

        await maintenance.CheckAsync(Ct);

        state.IsReady.ShouldBeFalse();
        (await ScalarAsync<int>(database, "SELECT COUNT(*) FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'dbo.Messages')")).ShouldBe(0);
        logger.Errors.ShouldHaveSingleItem().ShouldContain("PK_Messages");
    }

    /// <summary>
    /// Appends one message ("zebra seed", <c>Seq</c> 1, plus "the" for the stoplist test), then inserts the rest straight
    /// into the same session, each containing "zebra".
    /// </summary>
    private static async Task SeedMessagesAsync(SqlTestDatabase database, int count)
    {
        var store = database.CreateStore(TimeProvider.System);
        await store.Workspaces.GetOrCreateAsync("ws-fts", null, null, Ct);
        await store.Sessions.GetOrCreateAsync("ws-fts", "sess-fts", null, null, null, Ct);
        await store.Messages.AppendAsync("ws-fts", "sess-fts", [new NewMessage("bob", "zebra seed for the index", 1, null, null)], idempotency: null, Ct);
        await InsertMessagesAsync(database, 1, count - 1);
    }

    /// <summary>Inserts <paramref name="count"/> messages after <c>Seq</c> <paramref name="lastSeq"/>, copying the seed's keys.</summary>
    private static Task InsertMessagesAsync(SqlTestDatabase database, int lastSeq, int count) =>
        ExecuteAsync(
            database,
            $"""
            INSERT dbo.Messages (WorkspaceId, SessionId, PeerId, PublicId, Seq, Content, TokenCount, CreatedAt)
            SELECT TOP ({count}) m.WorkspaceId, m.SessionId, m.PeerId, CONCAT(N'bulk', {lastSeq} + n.n), {lastSeq} + n.n,
                   CONCAT(N'zebra number ', {lastSeq} + n.n, N' grazes'), 3, m.CreatedAt
            FROM dbo.Messages AS m
            CROSS JOIN (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS n
            WHERE m.Seq = 1
            """);

    private static Task WaitUntilReadyAsync(FullTextIndexMaintenance maintenance, FullTextIndexState state) =>
        WaitUntilAsync(async () =>
        {
            await maintenance.CheckAsync(Ct);
            return state.IsReady;
        });

    private static async Task WaitUntilAsync(Func<bool> condition) => await WaitUntilAsync(() => Task.FromResult(condition()));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow + ReadyTimeout;
        while (!await condition())
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the full-text index did not become ready");
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
    }

    private static async Task ExecuteAsync(SqlTestDatabase database, string sql)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(SqlTestDatabase database, string sql)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}

[Collection(SqlServerNoFtsDockerGroup.Name)]
public sealed class FullTextIndexMaintenanceNoFtsTests(SqlServerNoFtsFixture fixture)
{
    [Fact]
    public async Task NoFts_StaysOnLikeAndDoesNotThrow()
    {
        var database = await SqlTestDatabase.CreateAsync(fixture);
        database.Options.FullTextIndexPollInterval = TimeSpan.FromMilliseconds(50);
        var logger = new CollectingLogger();
        var state = new FullTextIndexState();
        var maintenance = database.CreateFullTextIndexMaintenance(state, TimeProvider.System, logger);

        await maintenance.CheckAsync(CancellationToken.None);
        await maintenance.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await maintenance.StopAsync(CancellationToken.None);

        state.IsReady.ShouldBeFalse();
        logger.Warnings.ShouldBeEmpty();
        logger.Errors.ShouldBeEmpty();
        logger.Information.ShouldHaveSingleItem().ShouldContain("not installed");

        var store = database.CreateStore(TimeProvider.System, state);
        await store.Workspaces.GetOrCreateAsync("ws", null, null, CancellationToken.None);
        await store.Sessions.GetOrCreateAsync("ws", "sess", null, null, null, CancellationToken.None);
        await store.Messages.AppendAsync("ws", "sess", [new NewMessage("bob", "plain words", 2, null, null)], idempotency: null, CancellationToken.None);
        var hits = await store.Search.LexicalAsync(
            new MessageSearchRequest("ws", new MessageSearchScope.Workspace(), null), ["words"], 10, CancellationToken.None);
        hits.ShouldHaveSingleItem().Message.Content.ShouldBe("plain words");
    }
}

/// <summary>Collects the messages logged at each level.</summary>
internal sealed class CollectingLogger : Microsoft.Extensions.Logging.ILogger<FullTextIndexMaintenance>
{
    private readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<string> Errors => Messages(Microsoft.Extensions.Logging.LogLevel.Error);

    public IReadOnlyList<string> Warnings => Messages(Microsoft.Extensions.Logging.LogLevel.Warning);

    public IReadOnlyList<string> Information => Messages(Microsoft.Extensions.Logging.LogLevel.Information);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => NullLogger.Instance.BeginScope(state);

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private List<string> Messages(Microsoft.Extensions.Logging.LogLevel level)
    {
        lock (_entries)
        {
            return [.. _entries.Where(entry => entry.Level == level).Select(entry => entry.Message)];
        }
    }
}
