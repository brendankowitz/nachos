using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Nachos.Abstractions.Domain;
using Shouldly;

namespace Nachos.Api.Tests.Auth;

public sealed class EntraTests
{
    [Theory]
    [InlineData("Nachos.Admin")]
    [InlineData("Nachos.Workspace")]
    public async Task RealSignatureAndIssuerValidation_MapsRolesWithoutTokenScopeInjection(string role)
    {
        using var entra = new OfflineEntra();
        using var host = new AuthHost { Entra = entra };
        await host.Seed();
        await host.Store.Grants.AddAsync(new("object-id", null, GrantRoles.Workspace), default);
        var token = entra.Token(role);
        await host.Send("POST", "/v3/workspaces", """{"id":"A"}""", token, 200);
        await host.Send("POST", "/v3/workspaces/list", "{}", token, role == GrantRoles.Admin ? 200 : 401);
        await host.Send("POST", "/v3/keys?workspace_id=A", null, token, role == GrantRoles.Admin ? 200 : 401);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("signature")]
    [InlineData("kid")]
    [InlineData("expiry")]
    [InlineData("oid")]
    [InlineData("role")]
    [InlineData("algorithm")]
    public async Task InvalidEntraCredential_IsRejectedOffline(string invalid)
    {
        using var entra = new OfflineEntra();
        using var host = new AuthHost { Entra = entra };
        await host.Store.Grants.AddAsync(new("object-id", null, GrantRoles.Workspace), default);
        await host.Send("POST", "/v3/workspaces", """{"id":"A"}""", entra.Token(invalid: invalid), 401);
    }

    [Theory]
    [InlineData("header-truncated-json")]
    [InlineData("header-array")]
    [InlineData("header-kid-number")]
    [InlineData("payload-exp-string")]
    [InlineData("payload-exp-overflow")]
    [InlineData("payload-iss-object")]
    public async Task MalformedEntraRoutedToken_IsUnauthorized_NotServerError(string malformed)
    {
        using var entra = new OfflineEntra();
        using var host = new AuthHost { Entra = entra };
        var (header, payload) = malformed switch
        {
            "header-truncated-json" => ("{", """{"iss":"x"}"""),
            "header-array" => ("[]", """{"iss":"x"}"""),
            "header-kid-number" => ("""{"alg":"RS256","kid":5}""", """{"iss":"x"}"""),
            "payload-exp-string" => ("""{"alg":"RS256","kid":"offline-rsa"}""", """{"iss":"x","exp":"abc"}"""),
            "payload-exp-overflow" => ("""{"alg":"RS256","kid":"offline-rsa"}""", """{"iss":"x","exp":99999999999999999999999}"""),
            _ => ("""{"alg":"RS256","kid":"offline-rsa"}""", """{"iss":{}}"""),
        };
        var token = $"{Base64UrlEncoder.Encode(header)}.{Base64UrlEncoder.Encode(payload)}.AAAA";
        await host.Send("POST", "/v3/workspaces/list", "{}", token, 401);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("io")]
    public async Task MetadataRetrievalFailure_RemainsServerError(string failure)
    {
        using var entra = new OfflineEntra();
        using var host = new AuthHost
        {
            Entra = entra,
            ServicesOverride = services => services.Configure<JwtBearerOptions>("Entra", options =>
                options.ConfigurationManager = new UnavailableMetadata(failure == "http"
                    ? new HttpRequestException("metadata unavailable")
                    : new IOException("metadata unavailable"))),
        };
        await host.Send("POST", "/v3/workspaces", """{"id":"A"}""", entra.Token(), 500);
    }

    [Fact]
    public async Task NamedGrantIsResolvedAgainAfterActualRevocation()
    {
        using var entra = new OfflineEntra();
        using var host = new AuthHost { Entra = entra };
        await host.Seed();
        var grant = new GrantRecord("object-id", "A", GrantRoles.Workspace);
        await host.Store.Grants.AddAsync(grant, default);
        var token = entra.Token();
        await host.Send("POST", "/v3/workspaces", """{"id":"A"}""", token, 200);
        await host.Send("POST", "/v3/workspaces", """{"id":"B"}""", token, 401);
        await host.Store.Grants.RemoveAsync(grant, default);
        await host.Send("POST", "/v3/workspaces", """{"id":"A"}""", token, 401);
        (await host.Store.Grants.ListAsync("object-id", default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task KeylessEntraAdminAuthenticates_ButCannotMintKeys()
    {
        using var entra = new OfflineEntra();
        using var host = new AuthHost { Entra = entra, SigningKeys = new() };
        var token = entra.Token(GrantRoles.Admin);
        await host.Send("POST", "/v3/workspaces", """{"id":"A"}""", token, 200);
        var error = await host.Send("POST", "/v3/keys?workspace_id=A", null, token, 422);
        error.GetProperty("detail").GetString().ShouldBe("key issuance requires configured signing keys");
    }

    private sealed class UnavailableMetadata(Exception failure) : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) => Task.FromException<OpenIdConnectConfiguration>(failure);
        public void RequestRefresh() { }
    }
}
