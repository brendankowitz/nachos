using Azure;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nachos.Core.Keys;
using Nachos.Testing;
using Shouldly;

namespace Nachos.Api.Tests.Auth;

public sealed class StartupTests
{
    [Fact]
    public async Task Vault_LoadsConfiguredKidsBeforeConstructingTheSingleIssuer()
    {
        var vault = new RecordingVault();
        using var host = VaultHost(vault, new() { Keys = [new("current", ""), new("previous", "")] });
        await host.Send("POST", "/v3/workspaces", """{"id":"A"}""", AuthHost.Key("admin"), 200);
        vault.Names.ShouldBe(["nachos-signing-key-current", "nachos-signing-key-previous"]);
        host.Services.GetRequiredService<IKeyIssuer>().ShouldBeSameAs(host.Services.GetRequiredService<IKeyIssuer>());
    }

    [Fact]
    public void UnreadableVaultSecret_FailsStartupWithoutFallback()
    {
        var failure = new IOException("synthetic offline vault failure");
        var vault = new RecordingVault { Failure = failure };
        using var host = VaultHost(vault, AuthHost.Ring);
        Should.Throw<IOException>(() => host.CreateClient()).ShouldBeSameAs(failure);
        vault.Names.ShouldBe(["nachos-signing-key-current"]);
    }

    [Fact]
    public async Task EmptyRing_DoesNotResolveVaultOrInventKeys()
    {
        var vault = new RecordingVault { Failure = new InvalidOperationException("must not resolve") };
        using var host = VaultHost(vault, new());
        using var response = await host.Http.GetAsync("/health");
        response.IsSuccessStatusCode.ShouldBeTrue();
        vault.Names.ShouldBeEmpty();
        Should.Throw<Nachos.Abstractions.NachosValidationException>(() =>
            host.Services.GetRequiredService<IKeyIssuer>().Issue(new(true, null, null, null, null)));
    }

    [Fact]
    public void ConfiguredInvalidRing_FailsBeforeReadiness()
    {
        using var host = new AuthHost { SigningKeys = new() { Keys = [new("bad", "short")] } };
        Should.Throw<OptionsValidationException>(() => host.CreateClient());
    }

    [Fact]
    public void DisabledAuthOutsideDevelopment_FailsStartup()
    {
        using var original = new NachosApiFactory();
        using var host = original.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        Should.Throw<InvalidOperationException>(() => host.CreateClient()).Message
            .ShouldBe("Authentication may be disabled only in Development.");
    }

    private static AuthHost VaultHost(RecordingVault vault, SigningKeyOptions ring) => new()
    {
        SigningKeys = ring,
        ConfigurationOverride = configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["AZURE_KEY_VAULT_ENDPOINT"] = "https://offline-vault.invalid/" }),
        ServicesOverride = services => services.AddSingleton<SecretClient>(vault),
    };

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
