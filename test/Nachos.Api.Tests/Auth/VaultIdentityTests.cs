using System.Net;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Nachos.Api.Tests.Auth;

[CollectionDefinition("Azure SDK defaults", DisableParallelization = true)]
public sealed class AzureSdkDefaults;

[Collection("Azure SDK defaults")]
public sealed class VaultIdentityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("33333333-3333-3333-3333-333333333333")]
    public async Task ProductionVaultFactory_SelectsConfiguredManagedIdentity_WithoutNetwork(string? clientId)
    {
        var previous = ClientOptions.Default.Transport;
        var vaultHost = "synthetic-" + Guid.NewGuid().ToString("N") + ".vault.azure.net";
        using var recording = new RecordingTransport(vaultHost);
        ClientOptions.Default.Transport = new HttpClientTransport(recording);
        try
        {
            using var host = new AuthHost
            {
                SigningKeys = new(),
                ConfigurationOverride = configuration => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AZURE_KEY_VAULT_ENDPOINT"] = "https://" + vaultHost + "/",
                        ["AZURE_CLIENT_ID"] = clientId,
                    }),
                ServicesOverride = services =>
                {
                    // Exercise the actual production factory, not the fixture's SecretClient replacement.
                    var factory = services.First(descriptor => descriptor.ServiceType == typeof(SecretClient) &&
                        descriptor.ImplementationFactory is not null).ImplementationFactory!;
                    services.AddSingleton(provider => (SecretClient)factory(provider));
                },
            };
            var vault = host.Services.GetRequiredService<SecretClient>();
            recording.Requests.ShouldBeEmpty();
            var failure = await Should.ThrowAsync<Exception>(() => vault.GetSecretAsync("synthetic-probe"));
            recording.Requests.ShouldContain(uri => uri.Host == vaultHost);
            var identityRequests = recording.Requests.Where(uri => uri.Host == "169.254.169.254").ToArray();
            identityRequests.ShouldNotBeEmpty(failure.ToString());
            foreach (var uri in identityRequests)
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
                if (clientId is null) query.ContainsKey("client_id").ShouldBeFalse();
                else
                {
                    query.TryGetValue("client_id", out var selected).ShouldBeTrue();
                    selected.ToString().ShouldBe(clientId);
                }
            }
        }
        finally
        {
            ClientOptions.Default.Transport = previous;
        }
    }

    private sealed class RecordingTransport(string vaultHost) : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri.ShouldNotBeNull();
            Requests.Add(uri);
            if (uri.Host == "169.254.169.254")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":"invalid_request","error_description":"Offline probe; no token issued."}"""),
                });
            if (uri.Host != vaultHost)
                throw new IOException("Offline identity transport stopped the request before network I/O.");
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            response.Headers.WwwAuthenticate.ParseAdd(
                """Bearer authorization="https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111", resource="https://vault.azure.net" """);
            return Task.FromResult(response);
        }
    }
}
