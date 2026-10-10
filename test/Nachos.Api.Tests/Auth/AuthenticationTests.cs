using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Nachos.Api.Tests.Auth;

public sealed class AuthenticationTests
{
    [Theory]
    [InlineData("admin")]
    [InlineData("workspace")]
    public async Task VerifiedKeys_CanCreateTheirWorkspace(string identity)
    {
        using var host = new AuthHost();
        (await host.Send("POST", "/v3/workspaces", """{"id":"A"}""", AuthHost.Key(identity), 200))
            .GetProperty("id").GetString().ShouldBe("A");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("other")]
    [InlineData("peer")]
    [InlineData("scoped-admin")]
    public async Task NarrowerOrMissingAuthority_CannotCreateWorkspace(string? identity)
    {
        using var host = new AuthHost();
        await host.Send("POST", "/v3/workspaces", """{"id":"A"}""",
            identity is null ? null : AuthHost.Key(identity), 401);
    }

    [Fact]
    public void EveryActualV3Endpoint_HasAuthorizationMetadata()
    {
        using var host = new AuthHost();
        _ = host.Http;
        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/v3", StringComparison.Ordinal)).ToArray();
        routes.Length.ShouldBe(56);
        foreach (var route in routes)
            route.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldNotBeEmpty(route.RoutePattern.RawText);
    }

    [Fact]
    public async Task AdminKey_CanMintScopedKeyButNotAnUnscopedAdminKey()
    {
        using var host = new AuthHost();
        var issued = await host.Send("POST", "/v3/keys?workspace_id=A", null, AuthHost.Key("admin"), 200);
        await host.Send("POST", "/v3/workspaces", """{"id":"A"}""", issued.GetProperty("key").GetString(), 200);
        await host.Send("POST", "/v3/keys", "{}", AuthHost.Key("admin"), 422);
    }
}
