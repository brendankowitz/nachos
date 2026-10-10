using Azure;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Nachos.Core.Keys;
using Nachos.Testing;
using Shouldly;

namespace Nachos.Api.Tests.Auth;

public sealed class VaultConfigurationTests
{
    [Fact]
    public void HydratedConfiguration_DoesNotOverwriteLaterProgrammaticRingReplacement()
    {
        var vault = new RecordingVault();
        using var host = new AuthHost
        {
            SigningKeys = new() { Keys = [new("override", AuthHost.Secret)] },
            ConfigurationOverride = configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AZURE_KEY_VAULT_ENDPOINT"] = "https://offline-vault.invalid/",
                    ["Nachos:Auth:NachosKey:Keys:0:Kid"] = "configured",
                }),
            ServicesOverride = services => services.AddSingleton<SecretClient>(vault),
        };
        var issuer = host.Services.GetRequiredService<IKeyIssuer>();
        var token = issuer.Issue(new(true, null, null, null, null));
        new JsonWebToken(token).Kid.ShouldBe("override");
        vault.Names.ShouldBe(["nachos-signing-key-configured", "nachos-signing-key-override"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("synthetic-task10-signing-secret-01234567890123456789")]
    public void ConfiguredVaultFailure_NeverFallsBackToMissingOrLocalSecret(string? localSecret)
    {
        var failure = new IOException("synthetic configured vault failure");
        var vault = new RecordingVault { Failure = failure };
        using var original = new NachosApiFactory();
        using var host = original.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Nachos:Auth:Enabled"] = "true",
                    ["AZURE_KEY_VAULT_ENDPOINT"] = "https://offline-vault.invalid/",
                    ["Nachos:Auth:NachosKey:Keys:0:Kid"] = "current",
                    ["Nachos:Auth:NachosKey:Keys:0:Secret"] = localSecret,
                }));
            builder.ConfigureServices(services => services.AddSingleton<SecretClient>(vault));
        });
        Should.Throw<IOException>(() => host.CreateClient()).ShouldBeSameAs(failure);
        vault.Names.ShouldBe(["nachos-signing-key-current"]);
    }

    [Theory]
    [InlineData("Nachos:Auth:NachosKey:Unexpected", "value")]
    [InlineData("Nachos:Auth:NachosKey:Keys", "scalar")]
    [InlineData("Nachos:Auth:NachosKey:Keys:0", "scalar")]
    [InlineData("Nachos:Auth:NachosKey:Keys:0:Extra", "value")]
    [InlineData("Nachos:Auth:NachosKey:Keys:0:Secret:Nested", "value")]
    [InlineData("Nachos:Auth:NachosKey:Keys:0:Kid:Nested", "value")]
    public void MalformedConfiguration_RetainsBinderDiagnostics(string path, string value)
    {
        using var original = new NachosApiFactory();
        using var host = original.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Nachos:Auth:Enabled"] = "true",
                    ["AZURE_KEY_VAULT_ENDPOINT"] = "https://offline-vault.invalid/",
                    ["Nachos:Auth:NachosKey:Keys:0:Kid"] = "current",
                    ["Nachos:Auth:NachosKey:Keys:0:Secret"] = AuthHost.Secret,
                    [path] = value,
                }));
            builder.ConfigureServices(services => services.AddSingleton<SecretClient>(new RecordingVault()));
        });
        var error = Should.Throw<OptionsValidationException>(() => host.CreateClient());
        error.OptionsType.ShouldBe(typeof(SigningKeyOptions));
        error.Message.ShouldNotContain(AuthHost.Secret);
        error.Message.ShouldBe(path switch
        {
            "Nachos:Auth:NachosKey:Unexpected" => "Signing key configuration must contain only a Keys section.",
            "Nachos:Auth:NachosKey:Keys" => "Keys must be an ordered key list.",
            _ => "Keys[0] requires an object with scalar Kid and Secret fields.",
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredKidOnlyOrMixedRing_PreservesRetrievalAndSigningOrder(bool firstHasLocalSecret)
    {
        var vault = new RecordingVault();
        using var original = new NachosApiFactory();
        using var host = original.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nachos:Auth:Enabled"] = "true",
                    ["AZURE_KEY_VAULT_ENDPOINT"] = "https://offline-vault.invalid/",
                    ["Nachos:Auth:NachosKey:Keys:10:Kid"] = "previous",
                    ["Nachos:Auth:NachosKey:Keys:2:Kid"] = "current",
                });
                if (firstHasLocalSecret)
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Nachos:Auth:NachosKey:Keys:2:Secret"] = AuthHost.Secret,
                    });
            });
            builder.ConfigureServices(services => services.AddSingleton<SecretClient>(vault));
        });
        using var http = host.CreateClient();
        vault.Names.ShouldBe(["nachos-signing-key-current", "nachos-signing-key-previous"]);
        var options = host.Services.GetRequiredService<IOptions<SigningKeyOptions>>().Value;
        options.Keys.Select(key => key.Kid).ShouldBe(["current", "previous"]);
        options.Keys.Select(key => key.Secret).ShouldAllBe(secret => secret == AuthHost.Secret);
        var issuer = host.Services.GetRequiredService<IKeyIssuer>();
        issuer.ShouldBeSameAs(host.Services.GetRequiredService<IKeyIssuer>());
        var token = issuer.Issue(new(true, null, null, null, null));
        new JsonWebToken(token).Kid.ShouldBe("current");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v3/workspaces")
        {
            Content = new StringContent("""{"id":"A"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new("Bearer", token);
        using var response = await http.SendAsync(request);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
    }

    private sealed class RecordingVault : SecretClient
    {
        internal List<string> Names { get; } = [];
        internal Exception? Failure { get; init; }
        public override Task<Response<KeyVaultSecret>> GetSecretAsync(string name, string? version = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Names.Add(name);
            if (Failure is not null) return Task.FromException<Response<KeyVaultSecret>>(Failure);
            return Task.FromResult(Response.FromValue(new KeyVaultSecret(name, AuthHost.Secret), null!));
        }
    }
}
