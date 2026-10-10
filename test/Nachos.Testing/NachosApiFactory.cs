using Azure;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.LoggingExtensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Nachos.Core.Keys;

namespace Nachos.Testing;

/// <summary>Hermetic Core/in-memory HTTP host, with explicit opt-in to real token authentication.</summary>
public class NachosApiFactory : WebApplicationFactory<Program>
{
    // IdentityModel caches its logger process-wide; it must not retain a disposed host's EventLog provider.
    private static readonly ILoggerFactory FixtureLogging = LoggerFactory.Create(logging => logging.AddConsole());

    static NachosApiFactory() =>
        LogHelper.Logger = new IdentityLoggerAdapter(FixtureLogging.CreateLogger("OfflineIdentityModel"));

    public WebApplicationFactory<Program> WithAuth(SigningKeyOptions signingKeys) => WithWebHostBuilder(builder =>
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Nachos:Auth:Enabled"] = "true" }));
        builder.ConfigureServices(services => services.Configure<SigningKeyOptions>(options => options.Keys = signingKeys.Keys));
    });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.Sources.Clear();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nachos:Auth:Enabled"] = "false",
                ["AZURE_KEY_VAULT_ENDPOINT"] = null,
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = null,
                ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = null,
            });
        });
        builder.ConfigureServices(services =>
        {
            services.AddNachos(nachos => nachos.UseInMemory());
            services.AddSingleton<SecretClient, OfflineVaultClient>();
            services.AddSingleton<IHttpClientFactory, OfflineClientFactory>();
            services.Configure<AadIssuerValidatorOptions>(options => options.HttpClientName = "Nachos.OfflineIssuer");
            services.Configure<JwtBearerOptions>("Entra", options =>
            {
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new());
                options.Backchannel = new HttpClient(new OfflineTransport());
            });
        });
    }

    private sealed class OfflineVaultClient : SecretClient
    {
        public override Task<Response<KeyVaultSecret>> GetSecretAsync(string name, string? version = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("An explicit offline vault client is required in tests.");
    }

    private sealed class OfflineClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new OfflineTransport());
    }

    private sealed class OfflineTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("An explicit offline authentication transport is required in tests.");
    }
}
