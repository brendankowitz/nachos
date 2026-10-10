using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Nachos.Api.Tests.Auth;

public sealed partial class ScopeMatrixTests
{
    internal const string W = "/v3/workspaces/{workspace_id}";
    internal const string P = W + "/peers/{peer_id}";
    internal const string S = W + "/sessions/{session_id}";
    internal const string M = S + "/messages";
    internal sealed record Route(string Method, string Template, string? Body, string Level, int Status = 200);
    internal static readonly Route[] Implemented =
    [
        new("POST", "/v3/workspaces", """{"id":"A"}""", "workspace"),
        new("POST", "/v3/workspaces/list", "{}", "admin"),
        new("POST", "/v3/keys", null, "admin"),
        new("POST", "/v3/admin/grants", """{"object_id":"subject","workspace_id":"A","role":"Nachos.Workspace"}""", "admin", 204),
        new("PUT", W, "{}", "workspace"),
        new("POST", W + "/peers", """{"id":"p1"}""", "workspace"),
        new("POST", W + "/peers/list", "{}", "workspace"),
        new("PUT", P, "{}", "peer"),
        new("POST", P + "/sessions", "{}", "peer"),
        new("POST", W + "/sessions", """{"id":"s1"}""", "workspace"),
        new("POST", W + "/sessions/list", "{}", "workspace"),
        new("PUT", S, "{}", "session"),
        new("POST", S + "/peers", """{"p1":{}}""", "session"),
        new("PUT", S + "/peers", """{"p1":{}}""", "session"),
        new("DELETE", S + "/peers", """["p1"]""", "session"),
        new("GET", S + "/peers", null, "member"),
        new("GET", S + "/peers/{peer_id}/config", null, "member"),
        new("PUT", S + "/peers/{peer_id}/config", "{}", "session", 204),
        new("POST", M, """{"messages":[{"content":"new","peer_id":"p1"}]}""", "session", 201),
        new("POST", M + "/list", "{}", "member"),
        new("GET", M + "/{message_id}", null, "member"),
        new("PUT", M + "/{message_id}", "{}", "session"),
    ];

    internal static Route[] AllRoutes()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "honcho-v3-wire.json")));
        var implemented = Implemented.Select(route => route.Method + " " + route.Template).ToHashSet(StringComparer.Ordinal);
        return [.. Implemented, .. manifest.RootElement.GetProperty("routes").EnumerateArray()
            .Select(route => new Route(route.GetProperty("method").GetString()!, route.GetProperty("path").GetString()!, "{}", "admin", 501))
            .Where(route => route.Template.StartsWith("/v3", StringComparison.Ordinal) &&
                !implemented.Contains(route.Method + " " + route.Template)),
            new("GET", W + "/jobs", null, "admin", 501)];
    }

    private static readonly string[] Identities =
        ["none", "admin", "workspace", "other", "peer", "nonmember", "session", "wrong-session", "scoped-admin"];

    public static IEnumerable<object[]> Cases() =>
        from route in AllRoutes()
        from identity in Identities
        select new object[] { route.Method, route.Template, identity };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ActualRouteAndVerifiedPrincipal_EnforceNarrowestScope(string method, string template, string identity)
    {
        var route = AllRoutes().Single(route => route.Method == method && route.Template == template);
        using var host = new AuthHost();
        await host.Seed();
        var messages = await host.Send("POST", Concrete(M), """{"messages":[{"content":"seed","peer_id":"p1"}]}""", AuthHost.Key("admin"), 201);
        var messageId = messages[0].GetProperty("id").GetString()!;
        var allowed = identity == "admin" || identity == "workspace" && route.Level != "admin" ||
            identity == "session" && route.Level is "session" or "member" ||
            identity is "peer" or "scoped-admin" && route.Level is "peer" or "member";
        await host.Send(method, Concrete(template, messageId), route.Body,
            identity == "none" ? null : AuthHost.Key(identity), allowed ? route.Status : 401);
    }

    [Fact]
    public void MatrixExactlyCoversEveryV3EndpointAndAuthorizationMetadata()
    {
        using var host = new AuthHost();
        _ = host.Http;
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/v3", StringComparison.Ordinal)).ToArray();
        var actual = endpoints.SelectMany(endpoint => endpoint.Metadata.GetRequiredMetadata<HttpMethodMetadata>().HttpMethods
            .Select(method => method + " " + endpoint.RoutePattern.RawText)).Order().ToArray();
        actual.Length.ShouldBe(56);
        actual.ShouldBe(AllRoutes().Select(route => route.Method + " " + route.Template).Order().ToArray());
        foreach (var endpoint in endpoints)
        {
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldNotBeEmpty();
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull();
        }
    }

    [Theory]
    [InlineData("/v3/unknown")]
    [InlineData("/V3/workspaces")]
    [InlineData("/v3/workspaces/")]
    [InlineData("/%76%33/workspaces")]
    [InlineData("/v3/workspaces/A/sessions/s1/messages/")]
    public async Task AnonymousUnknownCaseAndEncodedRoutes_FailClosed(string path)
    {
        using var host = new AuthHost();
        await host.Send("POST", path, "{}", null, 401);
    }

    [Fact]
    public async Task MemberRead_UsesCurrentMembershipAndConfigPeerEquality()
    {
        using var host = new AuthHost();
        await host.Seed();
        await host.Send("GET", Concrete(S + "/peers"), null, AuthHost.Key("peer"), 200);
        await host.Send("GET", Concrete(S + "/peers/p2/config"), null, AuthHost.Key("peer"), 401);
        await host.Store.Sessions.RemovePeersAsync("A", "s1", ["p1"], default);
        await host.Send("GET", Concrete(S + "/peers"), null, AuthHost.Key("peer"), 401);
        await host.Send("GET", "/v3/workspaces/A/sessions/missing/peers", null, AuthHost.Key("peer"), 401);
    }

    internal static string Concrete(string template, string message = "message") =>
        template == "/v3/keys" ? template + "?workspace_id=A" : Parameter().Replace(template, match => match.Value switch
        {
            "{workspace_id}" => "A", "{peer_id}" => "p1", "{session_id}" => "s1",
            "{message_id}" => message, _ => "missing",
        });

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex Parameter();
}
