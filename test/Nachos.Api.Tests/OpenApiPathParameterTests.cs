using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed class OpenApiPathParameterTests : ApiTest
{
    [Fact]
    public async Task EveryEmittedOperation_DeclaresExactlyItsMappedPathParameters()
    {
        var document = await Send(HttpMethod.Get, "/openapi/v1.json");
        var endpoints = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        var errors = new List<string>();
        var operations = 0;
        var staged = 0;
        foreach (var path in document.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (operation.Name is not ("get" or "put" or "post" or "delete" or "patch" or "head" or "options" or "trace"))
                {
                    continue;
                }
                operations++;
                var label = operation.Name + " " + path.Name;
                var endpoint = endpoints.Where(endpoint => endpoint.RoutePattern.RawText == path.Name &&
                    endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(operation.Name.ToUpperInvariant()))
                    .ShouldHaveSingleItem(label);
                var expected = endpoint.RoutePattern.Parameters.Select(parameter => parameter.Name).Order().ToArray();
                var parameters = Parameters(path.Value).Concat(Parameters(operation.Value))
                    .Where(parameter => parameter.GetProperty("in").GetString() == "path").ToArray();
                var actual = parameters.Select(parameter => parameter.GetProperty("name").GetString()!).Order().ToArray();
                if (!actual.SequenceEqual(expected))
                {
                    errors.Add(label + ": expected [" + string.Join(", ", expected) + "], actual [" + string.Join(", ", actual) + "]");
                }
                foreach (var parameter in parameters)
                {
                    parameter.GetProperty("required").GetBoolean().ShouldBeTrue(label);
                    parameter.GetProperty("schema").GetProperty("type").GetString().ShouldBe("string", label);
                }
                var responses = operation.Value.GetProperty("responses");
                if (responses.EnumerateObject().Count() == 1 && responses.TryGetProperty("501", out _))
                {
                    staged++;
                }
            }
        }
        operations.ShouldBeGreaterThan(0);
        staged.ShouldBeGreaterThan(0);
        errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/v3/workspaces/{workspace_id}", "put", "workspace_id")]
    [InlineData("/v3/workspaces/{workspace_id}/peers/{peer_id}", "put", "workspace_id,peer_id")]
    [InlineData("/v3/workspaces/{workspace_id}/sessions/{session_id}/messages/{message_id}", "get",
        "workspace_id,session_id,message_id")]
    public async Task ExistingImplementedPathParameters_RemainSingularRequiredStrings(string path, string method, string names)
    {
        var document = await Send(HttpMethod.Get, "/openapi/v1.json");
        var item = document.GetProperty("paths").GetProperty(path);
        var parameters = Parameters(item).Concat(Parameters(item.GetProperty(method))).ToArray();
        parameters.Select(parameter => parameter.GetProperty("name").GetString()).Order().ShouldBe(names.Split(',').Order());
        foreach (var parameter in parameters)
        {
            parameter.GetProperty("in").GetString().ShouldBe("path");
            parameter.GetProperty("required").GetBoolean().ShouldBeTrue();
            parameter.GetProperty("schema").GetProperty("type").GetString().ShouldBe("string");
        }
    }

    [Fact]
    public async Task ExistingPagingConstraintsAndStagedResponse_ArePreserved()
    {
        var document = await Send(HttpMethod.Get, "/openapi/v1.json");
        var paths = document.GetProperty("paths");
        var operation = paths.GetProperty("/v3/workspaces/{workspace_id}/sessions/{session_id}/messages/list").GetProperty("post");
        var query = Parameters(operation).Where(parameter => parameter.GetProperty("in").GetString() == "query")
            .ToDictionary(parameter => parameter.GetProperty("name").GetString()!, parameter => parameter.GetProperty("schema"));
        query["page"].GetProperty("minimum").GetInt32().ShouldBe(1);
        query["page"].GetProperty("default").GetInt32().ShouldBe(1);
        query["size"].GetProperty("maximum").GetInt32().ShouldBe(100);
        query["size"].GetProperty("default").GetInt32().ShouldBe(50);
        var responses = paths.GetProperty("/v3/workspaces/{workspace_id}").GetProperty("delete").GetProperty("responses");
        responses.EnumerateObject().Select(response => response.Name).ShouldBe(["501"]);
        responses.GetProperty("501").GetProperty("content").GetProperty("application/json").GetProperty("schema")
            .GetProperty("$ref").GetString().ShouldBe("#/components/schemas/ErrorResponse");
        var deferred = await Send(HttpMethod.Delete, W, status: 501);
        Problem(deferred, 501, JsonValueKind.String);
    }

    private static JsonElement[] Parameters(JsonElement scope) =>
        scope.TryGetProperty("parameters", out var parameters) ? parameters.EnumerateArray().ToArray() : [];
}
