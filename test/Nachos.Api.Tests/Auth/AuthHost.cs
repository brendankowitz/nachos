using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nachos.Abstractions.Stores;
using Nachos.Core.Keys;
using Nachos.Testing;
using Shouldly;

namespace Nachos.Api.Tests.Auth;

internal sealed class AuthHost : NachosApiFactory
{
    internal const string Secret = "synthetic-task10-signing-secret-01234567890123456789";
    internal static SigningKeyOptions Ring => new() { Keys = [new("current", Secret)] };
    internal Action<IServiceCollection>? ServicesOverride { get; set; }
    internal Action<IConfigurationBuilder>? ConfigurationOverride { get; set; }
    internal OfflineEntra? Entra { get; set; }
    internal SigningKeyOptions? SigningKeys { get; set; }
    internal IMemoryStore Store => Services.GetRequiredService<IMemoryStore>();
    private HttpClient? _http;
    internal HttpClient Http => _http ??= CreateClient();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.Sources.Clear();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nachos:Auth:Enabled"] = "true",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = null,
                ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = null,
                ["AZURE_KEY_VAULT_ENDPOINT"] = null,
            });
            Entra?.Configure(configuration);
            ConfigurationOverride?.Invoke(configuration);
        });
        builder.ConfigureServices(services =>
        {
            services.Configure<SigningKeyOptions>(options => options.Keys = (SigningKeys ?? Ring).Keys);
            Entra?.Configure(services);
            ServicesOverride?.Invoke(services);
        });
    }

    internal static string Key(string identity) => new HmacKeyIssuer(Options.Create(Ring), TimeProvider.System).Issue(identity switch
    {
        "admin" => new(true, null, null, null, null),
        "workspace" => new(false, "A", null, null, null),
        "other" => new(false, "B", null, null, null),
        "peer" => new(false, "A", "p1", null, null),
        "nonmember" => new(false, "A", "p2", null, null),
        "session" => new(false, "A", null, "s1", null),
        "wrong-session" => new(false, "A", null, "s2", null),
        "scoped-admin" => new(true, "A", "p1", null, null),
        _ => throw new ArgumentOutOfRangeException(nameof(identity)),
    });

    internal async Task<JsonElement> Send(string method, string path, string? body, string? token, int status)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).ShouldBe(status, text);
        if (status == 204) { text.ShouldBeEmpty(); return default; }
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    internal async Task Seed()
    {
        using var scope = Services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<Nachos.Abstractions.INachosClient>();
        await client.GetOrCreateWorkspaceAsync("A");
        await client.GetOrCreateWorkspaceAsync("B");
        await client.GetOrCreatePeerAsync("A", "p1");
        await client.GetOrCreatePeerAsync("A", "p2");
        await client.GetOrCreateSessionAsync("A", "s1", peers: new Dictionary<string, Nachos.Abstractions.Contracts.SessionPeerConfig>
        {
            ["p1"] = new(),
        });
    }
}
