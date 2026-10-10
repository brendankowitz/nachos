using Microsoft.AspNetCore.Http.HttpResults;
using Nachos.Abstractions.Contracts;
using Nachos.Api.Json;

namespace Nachos.Api.Endpoints;

internal static class NotImplementedEndpoints
{
    private const string W = "/v3/workspaces/{workspace_id}";
    private const string P = W + "/peers/{peer_id}";
    private const string S = W + "/sessions/{session_id}";
    private const string Q = W + "/scopes/{scope_id}";

    // Explicit method/path pairs: a typo or wrong verb must remain a 404/405, not a blanket 501.
    public static IReadOnlyList<(string Method, string Path)> Routes { get; } =
    [
        ("DELETE", W), ("POST", W + "/chat"),
        ("POST", W + "/conclusions"), ("POST", W + "/conclusions/list"), ("POST", W + "/conclusions/query"),
        ("DELETE", W + "/conclusions/{conclusion_id}"), ("GET", W + "/conclusions/{conclusion_id}"),
        ("GET", P + "/card"), ("PUT", P + "/card"), ("POST", P + "/chat"), ("GET", P + "/context"),
        ("POST", P + "/representation"), ("POST", P + "/search"), ("GET", W + "/queue/status"),
        ("POST", W + "/schedule_dream"), ("POST", W + "/scopes"), ("POST", W + "/scopes/list"),
        ("GET", Q), ("POST", Q + "/sessions"), ("POST", Q + "/sessions/list"),
        ("DELETE", Q + "/sessions/{session_id}"), ("GET", Q + "/status"), ("POST", W + "/search"),
        ("DELETE", S), ("POST", S + "/clone"), ("GET", S + "/context"), ("POST", S + "/messages/upload"),
        ("POST", S + "/search"), ("GET", S + "/summaries"),
        ("GET", W + "/webhooks"), ("POST", W + "/webhooks"), ("GET", W + "/webhooks/test"),
        ("DELETE", W + "/webhooks/{endpoint_id}"), ("GET", W + "/jobs"),
    ];

    public static void MapNotImplementedEndpoints(this IEndpointRouteBuilder endpoints)
    {
        foreach (var (method, path) in Routes)
        {
            endpoints.MapMethods(path, [method], Response).Produces<ErrorResponse>(501);
        }
    }

    public static JsonHttpResult<ErrorResponse> Response() => TypedResults.Json(
        new ErrorResponse("Not implemented in this Nachos version", "about:blank", "Not Implemented", 501),
        NachosJsonContext.Default.ErrorResponse, statusCode: 501);
}
