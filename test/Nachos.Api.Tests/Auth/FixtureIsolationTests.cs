using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Nachos.Testing;
using Shouldly;

namespace Nachos.Api.Tests.Auth;

public sealed class FixtureIsolationTests
{
    [Fact]
    public void InheritedApplicationConfiguration_IsRemovedBeforeStartup()
    {
        using var original = new InheritedFactory();
        using var factory = original.WithAuth(AuthHost.Ring);
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        configuration.GetSection("Nachos:Auth:Entra").Exists().ShouldBeFalse();
        configuration["AZURE_KEY_VAULT_ENDPOINT"].ShouldBeNull();
        factory.Services.GetRequiredService<Nachos.Core.Keys.IKeyIssuer>()
            .Validate(AuthHost.Key("admin")).Admin.ShouldBeTrue();
    }

    [Fact]
    public async Task DefaultExternalClients_FailLocallyBeforeAnyExplicitOverride()
    {
        using var original = new NachosApiFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nachos:Auth:Entra:Instance"] = "https://never-contact.invalid/",
                ["Nachos:Auth:Entra:TenantId"] = OfflineEntra.Tenant,
                ["Nachos:Auth:Entra:ClientId"] = OfflineEntra.Audience,
            })));
        var vault = factory.Services.GetService<SecretClient>().ShouldNotBeNull();
        await Should.ThrowAsync<InvalidOperationException>(() => vault.GetSecretAsync("synthetic"));
        var clients = factory.Services.GetRequiredService<IHttpClientFactory>();
        using var client = clients.CreateClient("Nachos.OfflineIssuer");
        await Should.ThrowAsync<InvalidOperationException>(() => client.GetAsync("https://never-contact.invalid/metadata"));
        var bearer = factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("Entra");
        var metadata = await bearer.ConfigurationManager.ShouldNotBeNull().GetConfigurationAsync(default);
        metadata.ShouldBeOfType<OpenIdConnectConfiguration>().SigningKeys.ShouldBeEmpty();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://never-contact.invalid/discovery");
        await Should.ThrowAsync<InvalidOperationException>(() => bearer.Backchannel.ShouldNotBeNull().SendAsync(request));
    }

    private sealed class InheritedFactory : NachosApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Nachos:Auth:Entra:Instance"] = "https://never-contact.invalid/",
                    ["Nachos:Auth:Entra:TenantId"] = "synthetic",
                    ["Nachos:Auth:Entra:ClientId"] = "synthetic",
                    ["AZURE_KEY_VAULT_ENDPOINT"] = "https://never-contact.invalid/",
                }));
            base.ConfigureWebHost(builder);
            // Base fixture installs deny transports before WithAuth can resolve the ring or handle requests.
        }
    }
}
