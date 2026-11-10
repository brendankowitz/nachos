using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DotNet.Testcontainers.Builders;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Benchmarks;
using Nachos.Core.Configuration;
using Nachos.DataLayer.SqlServer.Search;
using Testcontainers.MsSql;

// The M3 search benchmark (plan Task 10, risk R1). See README.md for how to run it and research/m3-search-benchmark.md
// for the recorded results.
var settings = BenchmarkSettings.Parse(args);
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
var ct = cancel.Token;

const string Main = "bench";
const string Small = "small";
const int Sessions = 1_000;
const int Peers = 50;
const int SmallMessages = 2_000;
const int Limit = MessageSearchOptions.DefaultLimit;
const int Take = 4 * Limit;
var fullTextDeadline = TimeSpan.FromMinutes(90);

MsSqlContainer? container = null;
var server = settings.Connection;
if (server is null)
{
    container = await StartContainerAsync(settings.MemoryGb, ct);
    server = container.GetConnectionString();
}

try
{
    var connectionString = await CreateDatabaseAsync(server, ct);
    var probes = new Probes(connectionString);
    var fullTextInstalled = await probes.IsFullTextInstalledAsync(ct);
    var report = new StringBuilder();
    Directory.CreateDirectory(settings.OutputDirectory);
    var reportPath = Path.Combine(settings.OutputDirectory, $"m3-search-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md");
    report.AppendLine("# M3 search benchmark results").AppendLine()
        .AppendLine(CultureInfo.InvariantCulture, $"- Host: {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical CPUs, " +
            $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024):N0} MB available to .NET, {RuntimeInformation.FrameworkDescription}")
        .AppendLine(CultureInfo.InvariantCulture, $"- Server: {await probes.ServerAsync(ct)}")
        .AppendLine(CultureInfo.InvariantCulture, $"- Container memory cap: {(container is null ? "none (external server)" : $"{settings.MemoryGb} GB")}")
        .AppendLine(CultureInfo.InvariantCulture, $"- Series: up to {settings.Queries} sequential queries after 2 warm-ups; W/search and S/search " +
            $"stop after {settings.SearchBudget.TotalSeconds:N0} s, the others after {settings.Budget.TotalSeconds:N0} s (at least 10 queries); " +
            $"limit = {Limit} (each retrieval takes {Take}). A query that hits the 30 s command timeout counts as 30,000 ms. " +
            $"A series of fewer than {SeriesRunner.IndicativeBelow} queries is marked indicative only.")
        .AppendLine(CultureInfo.InvariantCulture, $"- W/search, S/search and P/search use the product default Nachos:Search:MaxVectorCandidates = {DefaultCap()}; the store-level \"vector, workspace\" series compares every chunk.")
        .AppendLine(fullTextInstalled ? "" : "- **Full-Text Search is not installed: the fts series were skipped.**")
        .AppendLine();

    // Two containers over one database: "fts" runs the hosted services (the full-text index maintenance, so it searches with
    // CONTAINSTABLE once the index is ready); "like" never starts them, so its index state stays not-ready and it searches
    // with the LIKE fallback, exactly as on a server without Full-Text Search.
    await using var like = BuildServices(connectionString);
    await using var fts = BuildServices(connectionString);
    var ftsState = fts.GetRequiredService<FullTextIndexState>();
    var corpus = new Corpus(5_000);
    var main = await CreateWorkspaceAsync(like, connectionString, Main, Sessions, Peers, ct);
    var small = await CreateWorkspaceAsync(like, connectionString, Small, 1, 2, ct);
    var hosted = fts.GetServices<IHostedService>().ToList();
    foreach (var service in hosted)
    {
        await service.StartAsync(ct);
    }

    var seeder = new Seeder(connectionString, corpus, BenchmarkSettings.Seed);
    await seeder.SeedAsync(small, SmallMessages, ct);
    var queries = new QuerySets(corpus, main, Peers, BenchmarkSettings.Seed + 1, settings.Queries + 2);
    var runner = new SeriesRunner(settings, probes);
    foreach (var scale in settings.Scales)
    {
        Console.WriteLine($"Scale {scale:N0}");
        await seeder.SeedAsync(main, scale, ct);
        if (fullTextInstalled)
        {
            await WaitForReadyAsync(ct);
            await probes.WaitForFullTextAsync(fullTextDeadline, ct);
        }

        await MeasureAsync(scale, report, ct);
        await File.WriteAllTextAsync(reportPath, report.ToString(), ct);
        Console.WriteLine($"  results so far: {Path.GetFullPath(reportPath)}");
    }

    foreach (var service in Enumerable.Reverse(hosted))
    {
        await service.StopAsync(CancellationToken.None);
    }

    Console.WriteLine(report);

    async Task WaitForReadyAsync(CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (!ftsState.IsReady)
        {
            if (clock.Elapsed > fullTextDeadline)
            {
                throw new TimeoutException($"The full-text index was not ready within {fullTextDeadline}.");
            }

            Console.WriteLine("  waiting for the full-text index to be ready");
            await Task.Delay(TimeSpan.FromSeconds(10), token);
        }
    }

    // An fts series run while the index is not ready would silently measure LIKE instead.
    Task<SeriesResult> RunAsync(
        string name, string mode, TimeSpan budget, Func<int, CancellationToken, Task> query, CancellationToken token) =>
        mode == "fts" && !ftsState.IsReady
            ? throw new InvalidOperationException($"The full-text index stopped being ready before '{name}'.")
            : runner.RunAsync(name, budget, query, token);

    async Task MeasureAsync(long scale, StringBuilder text, CancellationToken token)
    {
        await using var ftsScope = fts.CreateAsyncScope();
        await using var likeScope = like.CreateAsyncScope();
        (string Mode, IServiceProvider Provider)[] clients = fullTextInstalled
            ? [("fts", ftsScope.ServiceProvider), ("like", likeScope.ServiceProvider)]
            : [("like", likeScope.ServiceProvider)];
        var results = new List<SeriesResult>();

        foreach (var (mode, provider) in clients)
        {
            var client = provider.GetRequiredService<INachosClient>();
            results.Add(await RunAsync($"W/search ({mode})", mode, settings.SearchBudget, (i, c) =>
                client.SearchWorkspaceAsync(Main, new WorkspaceMessageSearchOptions(queries.Text(i), Limit), c), token));
            results.Add(await RunAsync($"S/search ({mode})", mode, settings.SearchBudget, (i, c) =>
                client.SearchSessionAsync(Main, queries.Session(i), new MessageSearchOptions(queries.Text(i), Limit), c), token));
            results.Add(await RunAsync($"P/search ({mode})", mode, settings.Budget, (i, c) =>
                client.SearchPeerAsync(Main, queries.Peer(i), new MessageSearchOptions(queries.Text(i), Limit), c), token));
        }

        var store = likeScope.ServiceProvider.GetRequiredService<IMemoryStore>();
        results.Add(await runner.RunAsync("vector, workspace", settings.Budget, (i, c) =>
            store.Search.VectorAsync(Request(Main, new MessageSearchScope.Workspace()), queries.Vector(i), Take, null, c), token));
        foreach (var cap in settings.VectorCaps)
        {
            results.Add(await runner.RunAsync($"vector, workspace, newest {cap:N0} chunks", settings.Budget, (i, c) =>
                store.Search.VectorAsync(Request(Main, new MessageSearchScope.Workspace()), queries.Vector(i), Take, cap, c), token));
        }

        results.Add(await runner.RunAsync("vector, session", settings.Budget, (i, c) =>
            store.Search.VectorAsync(Request(Main, new MessageSearchScope.Session(queries.Session(i))), queries.Vector(i), Take, null, c), token));
        results.Add(await runner.RunAsync("vector, peer", settings.Budget, (i, c) =>
            store.Search.VectorAsync(Request(Main, new MessageSearchScope.PeerPerspective(queries.Peer(i))), queries.Vector(i), Take, null, c), token));

        foreach (var (mode, provider) in clients)
        {
            var search = provider.GetRequiredService<IMemoryStore>().Search;
            foreach (var (label, terms) in queries.TermSets)
            {
                results.Add(await RunAsync($"lexical {label}, workspace ({mode})", mode, settings.Budget, (i, c) =>
                    search.LexicalAsync(Request(Main, new MessageSearchScope.Workspace()), terms[i % terms.Count], Take, c), token));
            }

            results.Add(await RunAsync($"lexical 1 common term, {SmallMessages:N0}-message workspace ({mode})", mode, settings.Budget,
                (i, c) => search.LexicalAsync(Request(Small, new MessageSearchScope.Workspace()), [queries.Common(i)], Take, c), token));
        }

        results.Add(await runner.RunAsync("reconciler ListUnchunkedMessagesAsync(500), nothing unchunked", settings.Budget, (_, c) =>
            store.Embeddings.ListUnchunkedMessagesAsync(500, c), token));

        text.AppendLine(CultureInfo.InvariantCulture, $"## {scale:N0} messages in the main workspace").AppendLine()
            .AppendLine("| Table | Rows | Reserved MB |").AppendLine("| --- | ---: | ---: |");
        foreach (var (table, rows, reserved) in await probes.TableSizesAsync(token))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {table} | {rows:N0} | {reserved:N0} |");
        }

        text.AppendLine().AppendLine("| Series | n | timeouts | p50 ms | p95 ms | max ms | avg logical reads | avg CPU ms | DOP | index operations of the slowest statement |")
            .AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
        foreach (var r in results)
        {
            var s = r.Statement;
            var name = r.IsIndicative ? $"{r.Name}, indicative only (n < {SeriesRunner.IndicativeBelow})" : r.Name;
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {name} | {r.Count} | {r.Timeouts} | {r.P50Ms:N1} | {r.P95Ms:N1} | {r.MaxMs:N1} | {s?.LogicalReads:N0} | {s?.CpuMs:N1} | {s?.Dop} | {s?.IndexOperations} |");
        }

        if (fullTextInstalled)
        {
            text.AppendLine().AppendLine("| CONTAINSTABLE condition | rows, all workspaces | rows in the main workspace | rows in the small workspace |")
                .AppendLine("| --- | ---: | ---: | ---: |");
            IReadOnlyList<IReadOnlyList<string>> common = [[queries.Common(0)]];
            foreach (var (label, terms) in queries.TermSets.Prepend(("1 common term", common)))
            {
                var condition = string.Join(" OR ", terms[0].Select(term => $"\"{term}*\""));
                var (all, inMain) = await probes.FullTextCandidatesAsync(condition, main.Id, token);
                var (_, inSmall) = await probes.FullTextCandidatesAsync(condition, small.Id, token);
                text.AppendLine(CultureInfo.InvariantCulture, $"| {label}: `{condition}` | {all:N0} | {inMain:N0} | {inSmall:N0} |");
            }
        }

        text.AppendLine();
    }
}
finally
{
    if (container is not null)
    {
        // A failed removal (for example after the Docker engine restarted) must not hide the error that ended the run.
        try
        {
            await container.DisposeAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Removing the SQL Server container failed: {ex.Message}");
        }
    }
}

static MessageSearchRequest Request(string workspace, MessageSearchScope scope) => new(workspace, scope, null);

static string DefaultCap() =>
    new NachosOptions().Search.MaxVectorCandidates?.ToString("N0", CultureInfo.InvariantCulture) ?? "none";

static ServiceProvider BuildServices(string connectionString)
{
    var services = new ServiceCollection();
    // The option sections bind from configuration; empty configuration leaves every default in place.
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddNachos(nachos => nachos
        .UseSqlServer(options =>
        {
            options.ConnectionString = connectionString;
            options.AutomaticSchemaDeploymentEnabled = true;
            options.FullTextIndexPollInterval = TimeSpan.FromSeconds(5);
        })
        .UseFakeAI());
    return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
}

static async Task<MsSqlContainer> StartContainerAsync(int memoryGb, CancellationToken ct)
{
    var image = new ImageFromDockerfileBuilder()
        .WithDockerfileDirectory(RepositoryPath("eng", "docker", "mssql-fts"))
        .WithDockerfile("Dockerfile")
        .WithName("nachos-test-mssql-fts:local")
        .WithDeleteIfExists(false)
        .Build();
    await image.CreateAsync(ct);

    // The cgroup limit is what protects the host. SQL Server gets 2 GB less, which leaves room for the full-text daemon
    // (fdhost) and the rest of the container outside the engine's own memory manager.
    var bytes = memoryGb * 1024L * 1024 * 1024;
    var container = new MsSqlBuilder(image)
        .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", ((memoryGb - 2) * 1024).ToString(CultureInfo.InvariantCulture))
        .WithCreateParameterModifier(parameters =>
        {
            parameters.HostConfig ??= new Docker.DotNet.Models.HostConfig();
            parameters.HostConfig.Memory = bytes;
            parameters.HostConfig.MemorySwap = bytes;
        })
        .Build();
    await container.StartAsync(ct);
    return container;
}

static async Task<string> CreateDatabaseAsync(string server, CancellationToken ct)
{
    var name = $"nachos_bench_{DateTime.UtcNow:yyyyMMddHHmmss}";
    await using (var connection = new SqlConnection(server))
    {
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand($"CREATE DATABASE [{name}]; ALTER DATABASE [{name}] SET RECOVERY SIMPLE;", connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    return new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString;
}

static async Task<SeedWorkspace> CreateWorkspaceAsync(
    IServiceProvider services, string connectionString, string name, int sessions, int peers, CancellationToken ct)
{
    await using (var scope = services.CreateAsyncScope())
    {
        var client = scope.ServiceProvider.GetRequiredService<INachosClient>();
        await client.GetOrCreateWorkspaceAsync(name, ct: ct);
        for (var p = 0; p < peers; p++)
        {
            await client.GetOrCreatePeerAsync(name, $"p{p}", ct: ct);
        }

        for (var s = 0; s < sessions; s++)
        {
            var (first, second) = SessionPeers(s, peers);
            await client.GetOrCreateSessionAsync(name, $"s{s}", ct: ct);
            await client.AddSessionPeersAsync(name, $"s{s}",
                new Dictionary<string, SessionPeerConfig> { [$"p{first}"] = new(), [$"p{second}"] = new() }, ct);
        }
    }

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync(ct);
    await using var command = new SqlCommand(
        """
        DECLARE @workspace bigint = (SELECT Id FROM dbo.Workspaces WHERE Name = @name);
        -- Seeded messages are dated from the benchmark's start; every membership window must already be open by then.
        UPDATE dbo.SessionPeers SET JoinedAt = '2000-01-01' WHERE WorkspaceId = @workspace;
        SELECT @workspace;
        SELECT s.Name, s.Id FROM dbo.Sessions AS s WHERE s.WorkspaceId = @workspace;
        SELECT p.Name, p.Id FROM dbo.Peers AS p WHERE p.WorkspaceId = @workspace;
        """, connection);
    command.Parameters.AddWithValue("@name", name);
    await using var reader = await command.ExecuteReaderAsync(ct);
    await reader.ReadAsync(ct);
    var workspaceId = reader.GetInt64(0);
    await reader.NextResultAsync(ct);
    var sessionIds = new Dictionary<string, long>();
    while (await reader.ReadAsync(ct))
    {
        sessionIds[reader.GetString(0)] = reader.GetInt64(1);
    }

    await reader.NextResultAsync(ct);
    var peerIds = new Dictionary<string, long>();
    while (await reader.ReadAsync(ct))
    {
        peerIds[reader.GetString(0)] = reader.GetInt64(1);
    }

    var seeded = Enumerable.Range(0, sessions).Select(s =>
    {
        var (first, second) = SessionPeers(s, peers);
        return new SeedSession($"s{s}", sessionIds[$"s{s}"], peerIds[$"p{first}"], peerIds[$"p{second}"]);
    });
    return new SeedWorkspace(name, workspaceId, [.. seeded]);
}

// Two distinct peers per session, spread so every peer is in about the same number of sessions.
static (int First, int Second) SessionPeers(int session, int peers) =>
    (session % peers, (session % peers + 1 + session / peers % (peers - 1)) % peers);

static string RepositoryPath(params string[] segments)
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        var candidate = Path.Combine([directory.FullName, .. segments]);
        if (Directory.Exists(candidate))
        {
            return candidate;
        }
    }

    throw new DirectoryNotFoundException($"No '{Path.Combine(segments)}' directory above {AppContext.BaseDirectory}.");
}
