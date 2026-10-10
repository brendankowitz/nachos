extern alias AzureCore;

using ManagedIdentityCredential = AzureCore::Azure.Identity.ManagedIdentityCredential;
using ManagedIdentityId = AzureCore::Azure.Identity.ManagedIdentityId;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Options;
using Nachos.Core.Keys;

namespace Nachos.Api.Auth;

internal static class SigningKeyVault
{
    internal static void AddSigningKeyVault(this IServiceCollection services) =>
        services.AddSingleton(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var clientId = configuration["AZURE_CLIENT_ID"];
            var identity = string.IsNullOrEmpty(clientId)
                ? ManagedIdentityId.SystemAssigned
                : ManagedIdentityId.FromUserAssignedClientId(clientId);
            return new SecretClient(new Uri(configuration["AZURE_KEY_VAULT_ENDPOINT"]!),
                new ManagedIdentityCredential(identity));
        });

    internal static async Task LoadAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        if (string.IsNullOrEmpty(configuration["AZURE_KEY_VAULT_ENDPOINT"])) return;
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        // Hydrate the entire configured ring in order before the unchanged binder runs.
        foreach (var entry in configuration.GetSection("Nachos:Auth:NachosKey:Keys").GetChildren())
        {
            if (entry["Kid"] is { } kid)
                entry["Secret"] = await ReadSecretAsync(kid);
        }
        var options = services.GetRequiredService<IOptions<SigningKeyOptions>>().Value;
        if (options.Keys.Count == 0) return;
        var loaded = new List<SigningKey>(options.Keys.Count);
        foreach (var key in options.Keys)
        {
            loaded.Add(key with { Secret = await ReadSecretAsync(key.Kid) });
        }
        options.Keys = loaded;

        async Task<string> ReadSecretAsync(string? kid)
        {
            var name = "nachos-signing-key-" + kid;
            if (secrets.TryGetValue(name, out var value)) return value;
            var secret = await services.GetRequiredService<SecretClient>()
                .GetSecretAsync(name, cancellationToken: cancellationToken);
            secrets.Add(name, secret.Value.Value);
            return secret.Value.Value;
        }
    }
}
