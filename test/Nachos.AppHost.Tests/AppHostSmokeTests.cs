using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Data.SqlClient;
using Shouldly;

namespace Nachos.AppHost.Tests;

/// <summary>
/// Starts the whole AppHost (SQL Server 2025 container plus the API) and exercises the API through the endpoint
/// Aspire assigns it. Needs Docker, like the SQL Server suite.
/// </summary>
public sealed class AppHostSmokeTests
{
    private static readonly TimeSpan DatabaseTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromMinutes(2);
    // Covers the database wait, the API wait and the requests that follow, with margin.
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(7);

    /// <summary>
    /// The API receives the <c>nachos</c> connection string from the AppHost, so it selects SQL Server: the first
    /// readiness check deploys the schema (the AppHost enables automatic deployment in Development), and the workspace
    /// written through the API is read back through the API and found in the database itself. The API's fail-closed
    /// auth gate is switched off here (this test only, in Development) because the key-based authentication is not wired yet.
    /// The run is isolated from the developer's machine: it gets its own throwaway SQL data (the persistent volume
    /// the AppHost uses for <c>dotnet run</c> is dropped) and its own SA password (so nothing is written to user
    /// secrets and a volume left by an earlier run cannot reject the login).
    /// </summary>
    [Fact]
    public async Task ApiReady_AndWorkspaceRoundTrip()
    {
        using var cts = new CancellationTokenSource(StartupTimeout);
        // Development is stated explicitly: the AppHost passes its environment on to the API, and the auth override below is Development-only.
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Nachos_AppHost>(
            ["--environment=Development", $"--Parameters:sql-password={NewSqlPassword()}"], cts.Token);

        var sql = appHost.Resources.Single(resource => resource.Name == "sql");
        foreach (var mount in sql.Annotations.OfType<ContainerMountAnnotation>().ToList())
        {
            sql.Annotations.Remove(mount);
        }
        appHost.CreateResourceBuilder<ProjectResource>("api")
            .WithEnvironment("Nachos__Auth__Enabled", "false");

        await using var app = await appHost.BuildAsync(cts.Token);
        // StartAsync returns only once every resource has started, and the API starts only after the database is healthy,
        // so it is awaited alongside the bounded, named database wait rather than before it.
        var starting = app.StartAsync(cts.Token);
        try
        {
            await WaitHealthyAsync(app, "nachos", DatabaseTimeout, cts.Token);
        }
        catch
        {
            await cts.CancelAsync();
            await starting.ContinueWith(_ => { }, TaskScheduler.Default);
            throw;
        }
        await starting;        await WaitHealthyAsync(app, "api", ApiTimeout, cts.Token);

        using var client = app.CreateHttpClient("api");
        using var ready = await client.GetAsync("/health/ready", cts.Token);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);

        var id = $"smoke-{Guid.NewGuid():N}";
        using var created = await client.PostAsJsonAsync("/v3/workspaces", new { id }, cts.Token);
        created.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Read back through the API...
        using var listed = await client.PostAsJsonAsync("/v3/workspaces/list", new { }, cts.Token);
        listed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await listed.Content.ReadAsStringAsync(cts.Token)).ShouldContain($"\"{id}\"");

        // ...and in the database, which also shows the schema was deployed and that the SQL provider served the request.
        var connectionString = await app.GetConnectionStringAsync("nachos", cts.Token);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cts.Token);
        await using var command = new SqlCommand(
            "SELECT (SELECT COUNT(*) FROM dbo.SchemaVersion), (SELECT COUNT(*) FROM dbo.Workspaces WHERE Name = @id)", connection);
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(cts.Token);
        (await reader.ReadAsync(cts.Token)).ShouldBeTrue();
        reader.GetInt32(0).ShouldBe(1, "the schema version row of a deployed database");
        reader.GetInt32(1).ShouldBe(1, "the workspace row written through the API");
    }

    private static async Task WaitHealthyAsync(DistributedApplication app, string resource, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await app.ResourceNotifications.WaitForResourceHealthyAsync(resource, limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Resource '{resource}' was not healthy within {timeout}.");
        }
    }

    // SQL Server rejects weak passwords; the fixed prefix guarantees upper, lower, digit and symbol.
    private static string NewSqlPassword() => "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}
