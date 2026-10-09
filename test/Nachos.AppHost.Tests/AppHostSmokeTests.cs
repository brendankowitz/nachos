using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Shouldly;

namespace Nachos.AppHost.Tests;

/// <summary>
/// Starts the whole AppHost (SQL Server 2025 container plus the API) and exercises the API through the endpoint
/// Aspire assigns it. Needs Docker, like the SQL Server suite.
/// </summary>
public sealed class AppHostSmokeTests
{
    private static readonly TimeSpan DatabaseTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The round trip runs against whichever provider the API selects. Until the API chooses SQL Server from the
    /// <c>nachos</c> connection string, that is the in-memory provider, and the API's fail-closed auth gate is
    /// switched off here (this test only, in Development) because the key-based authentication is not wired yet.
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
        await app.StartAsync(cts.Token);
        // The API waits for the database, so a database that never comes up is reported by name instead of as a bare timeout.
        await WaitHealthyAsync(app, "nachos", DatabaseTimeout, cts.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("api", cts.Token);

        using var client = app.CreateHttpClient("api");
        using var ready = await client.GetAsync("/health/ready", cts.Token);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var created = await client.PostAsJsonAsync("/v3/workspaces", new { id = $"smoke-{Guid.NewGuid():N}" }, cts.Token);
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
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
