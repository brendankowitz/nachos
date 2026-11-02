using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed partial class WireCoverageTests : ApiTest
{
    private static readonly HashSet<string> Implemented = new(StringComparer.Ordinal)
    {
        "GET /health",
        "POST /v3/keys", "POST /v3/admin/grants",
        "POST /v3/workspaces", "POST /v3/workspaces/list", "PUT /v3/workspaces/{workspace_id}",
        "POST /v3/workspaces/{workspace_id}/peers", "POST /v3/workspaces/{workspace_id}/peers/list",
        "PUT /v3/workspaces/{workspace_id}/peers/{peer_id}", "POST /v3/workspaces/{workspace_id}/peers/{peer_id}/sessions",
        "POST /v3/workspaces/{workspace_id}/sessions", "POST /v3/workspaces/{workspace_id}/sessions/list",
        "PUT /v3/workspaces/{workspace_id}/sessions/{session_id}",
        "POST /v3/workspaces/{workspace_id}/sessions/{session_id}/peers",
        "PUT /v3/workspaces/{workspace_id}/sessions/{session_id}/peers",
        "DELETE /v3/workspaces/{workspace_id}/sessions/{session_id}/peers",
        "GET /v3/workspaces/{workspace_id}/sessions/{session_id}/peers",
        "GET /v3/workspaces/{workspace_id}/sessions/{session_id}/peers/{peer_id}/config",
        "PUT /v3/workspaces/{workspace_id}/sessions/{session_id}/peers/{peer_id}/config",
        "POST /v3/workspaces/{workspace_id}/sessions/{session_id}/messages",
        "POST /v3/workspaces/{workspace_id}/sessions/{session_id}/messages/list",
        "GET /v3/workspaces/{workspace_id}/sessions/{session_id}/messages/{message_id}",
        "PUT /v3/workspaces/{workspace_id}/sessions/{session_id}/messages/{message_id}",
        "POST /v3/workspaces/{workspace_id}/conclusions", "POST /v3/workspaces/{workspace_id}/conclusions/list",
        "POST /v3/workspaces/{workspace_id}/conclusions/query",
        "GET /v3/workspaces/{workspace_id}/conclusions/{conclusion_id}",
        "DELETE /v3/workspaces/{workspace_id}/conclusions/{conclusion_id}",
        "GET /v3/workspaces/{workspace_id}/peers/{peer_id}/card", "PUT /v3/workspaces/{workspace_id}/peers/{peer_id}/card",
        "GET /v3/workspaces/{workspace_id}/peers/{peer_id}/context",
        "POST /v3/workspaces/{workspace_id}/peers/{peer_id}/representation",
        "GET /v3/workspaces/{workspace_id}/queue/status",
    };

    /// <summary>The routes M2 implemented: exactly these left the 501 list (plan Task 8).</summary>
    private static readonly (string Method, string Path)[] M2Routes =
    [
        ("POST", "/v3/workspaces/{workspace_id}/conclusions"), ("POST", "/v3/workspaces/{workspace_id}/conclusions/list"),
        ("POST", "/v3/workspaces/{workspace_id}/conclusions/query"),
        ("GET", "/v3/workspaces/{workspace_id}/conclusions/{conclusion_id}"),
        ("DELETE", "/v3/workspaces/{workspace_id}/conclusions/{conclusion_id}"),
        ("GET", "/v3/workspaces/{workspace_id}/peers/{peer_id}/card"), ("PUT", "/v3/workspaces/{workspace_id}/peers/{peer_id}/card"),
        ("GET", "/v3/workspaces/{workspace_id}/peers/{peer_id}/context"),
        ("POST", "/v3/workspaces/{workspace_id}/peers/{peer_id}/representation"),
        ("GET", "/v3/workspaces/{workspace_id}/queue/status"),
    ];

    [Fact]
    public void ImplementedSet_IsM1PlusExactlyTheM2Pairs()
    {
        // M1 implemented 22 manifest routes plus the grants extension; every other manifest route must still answer 501
        // (EveryManifestRoute_IsMapped).
        Implemented.Count.ShouldBe(23 + M2Routes.Length);
        M2Routes.ShouldAllBe(route => Implemented.Contains(route.Method + " " + route.Path));
        var manifest = Routes().Select(route => (string)route[0] + " " + (string)route[1]).ToHashSet(StringComparer.Ordinal);
        Implemented.Where(route => route != "POST /v3/admin/grants").ShouldAllBe(route => manifest.Contains(route));
    }

    public static TheoryData<string, string> M2RouteData()
    {
        var data = new TheoryData<string, string>();
        foreach (var (method, path) in M2Routes)
        {
            data.Add(method, path);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(M2RouteData))]
    public async Task M2Route_DocumentsExactlyTheManifestQueryParameters(string method, string path)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "honcho-v3-wire.json")));
        var expected = manifest.RootElement.GetProperty("routes").EnumerateArray()
            .Single(route => route.GetProperty("method").GetString() == method && route.GetProperty("path").GetString() == path)
            .GetProperty("query").EnumerateArray().Select(name => name.GetString()!).Order().ToArray();
        var operation = (await Send(HttpMethod.Get, "/openapi/v1.json")).GetProperty("paths").GetProperty(path)
            .GetProperty(method.ToLowerInvariant());
        var actual = operation.TryGetProperty("parameters", out var parameters)
            ? parameters.EnumerateArray().Where(p => p.GetProperty("in").GetString() == "query")
                .Select(p => p.GetProperty("name").GetString()!).Order().ToArray()
            : [];
        actual.ShouldBe(expected);
        var statuses = operation.GetProperty("responses").EnumerateObject().Select(r => r.Name).ToArray();
        statuses.ShouldContain("422");
        statuses.ShouldNotContain("501");
    }

    public static IEnumerable<object[]> Routes()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "honcho-v3-wire.json")));
        return manifest.RootElement.GetProperty("routes").EnumerateArray()
            .Select(route => new object[] { route.GetProperty("method").GetString()!, route.GetProperty("path").GetString()! })
            .ToArray();
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryManifestRoute_IsMapped(string method, string path)
    {
        _ = Client;
        var endpoints = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        endpoints.ShouldContain(endpoint => endpoint.RoutePattern.RawText == path &&
            (endpoint.Metadata.GetMetadata<HttpMethodMetadata>() == null ||
             endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method)));
        if (!Implemented.Contains(method + " " + path))
        {
            var body = await Send(new HttpMethod(method), Parameter().Replace(path, "missing"),
                method is "GET" or "DELETE" ? null : "{}", 501);
            body.GetProperty("detail").GetString().ShouldBe("Not implemented in this Nachos version");
        }
    }

    [Fact]
    public async Task UnknownRoute_IsNotMisrepresentedAs501()
    {
        await Send(HttpMethod.Post, W + "/typo", "{}", 404);
        await Send(HttpMethod.Put, W + "/chat", "{}", 405);
    }

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex Parameter();
}
