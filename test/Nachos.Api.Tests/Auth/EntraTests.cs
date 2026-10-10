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
}
