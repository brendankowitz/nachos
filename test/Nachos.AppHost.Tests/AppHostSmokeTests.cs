using System.Net;
using System.Net.Http.Json;
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
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The round trip runs against whichever provider the API selects. Until the API chooses SQL Server from the
    /// <c>nachos</c> connection string, that is the in-memory provider, and the API's fail-closed auth gate is
    /// switched off here (this test only, in Development) because the key-based authentication is not wired yet.
    /// </summary>
    [Fact]
    public async Task ApiReady_AndWorkspaceRoundTrip()
    {
        using var cts = new CancellationTokenSource(StartupTimeout);
        // Development is stated explicitly: the AppHost passes its environment on to the API, and the auth override below is Development-only.
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Nachos_AppHost>(["--environment=Development"], cts.Token);
        appHost.CreateResourceBuilder<ProjectResource>("api")
            .WithEnvironment("Nachos__Auth__Enabled", "false");

        await using var app = await appHost.BuildAsync(cts.Token);
        await app.StartAsync(cts.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("api", cts.Token);

        using var client = app.CreateHttpClient("api");
        using var ready = await client.GetAsync("/health/ready", cts.Token);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var created = await client.PostAsJsonAsync("/v3/workspaces", new { id = $"smoke-{Guid.NewGuid():N}" }, cts.Token);
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}



