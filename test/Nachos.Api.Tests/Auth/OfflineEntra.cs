using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Nachos.Api.Tests.Auth;

internal sealed class OfflineEntra : IDisposable
{
    internal const string Tenant = "11111111-1111-1111-1111-111111111111";
    internal const string Audience = "22222222-2222-2222-2222-222222222222";
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly string _instance = "https://offline-" + Guid.NewGuid().ToString("N") + ".invalid/";
    internal string Issuer => _instance + Tenant + "/v2.0";
    internal RsaSecurityKey Key => new(_rsa) { KeyId = "offline-rsa" };
    internal int DiscoveryReads { get; private set; }

    internal void Configure(IConfigurationBuilder configuration) => configuration.AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["Nachos:Auth:Entra:Instance"] = _instance,
            ["Nachos:Auth:Entra:TenantId"] = Tenant,
            ["Nachos:Auth:Entra:ClientId"] = Audience,
        });

    internal void Configure(IServiceCollection services)
    {
        services.AddSingleton<IHttpClientFactory>(new LocalClientFactory(this));
        services.Configure<AadIssuerValidatorOptions>(options => options.HttpClientName = "Nachos.OfflineIssuer");
        services.Configure<JwtBearerOptions>("Entra", options =>
        {
            var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
            metadata.SigningKeys.Add(Key);
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
            options.Backchannel = new HttpClient(new LocalMetadata(this));
        });
    }

    internal string Token(string role = "Nachos.Workspace", string? invalid = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["tid"] = Tenant, ["oid"] = "object-id", ["roles"] = new[] { role },
            ["ver"] = "2.0", ["p"] = "injected-peer", ["ad"] = true,
        };
        if (invalid == "oid") claims.Remove("oid");
        if (invalid == "role") claims["roles"] = new[] { "Other.Role" };
        using var wrongKey = invalid == "signature" ? RSA.Create(2048) : null;
        SecurityKey signingKey = wrongKey is not null ? new RsaSecurityKey(wrongKey) { KeyId = Key.KeyId } : Key;
        if (invalid == "kid") signingKey.KeyId = "unknown";
        var signing = invalid == "algorithm"
            ? new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AuthHost.Secret)), SecurityAlgorithms.HmacSha256)
            : new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = invalid == "issuer" ? "https://wrong.invalid/" + Tenant + "/v2.0" : Issuer,
            Audience = invalid == "audience" ? "wrong" : Audience,
            Claims = claims, IssuedAt = DateTime.UtcNow.AddHours(-1), NotBefore = DateTime.UtcNow.AddHours(-1),
            Expires = invalid == "expiry" ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = signing,
        });
    }

    public void Dispose() => _rsa.Dispose();

    private sealed class LocalClientFactory(OfflineEntra owner) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new LocalMetadata(owner));
    }

    private sealed class LocalMetadata(OfflineEntra owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.DiscoveryReads++;
            if (request.RequestUri?.Host != new Uri(owner.Issuer).Host)
                throw new InvalidOperationException("Unexpected offline discovery host.");
            var issuer = request.RequestUri.AbsolutePath.Contains("/v2.0/", StringComparison.Ordinal)
                ? owner.Issuer : owner._instance + Tenant + "/";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { issuer }), Encoding.UTF8, "application/json"),
            });
        }
    }
}
