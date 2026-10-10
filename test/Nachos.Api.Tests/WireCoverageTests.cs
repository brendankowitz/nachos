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
    };

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
