using System.Text.Json.Nodes;
using Shouldly;

namespace Nachos.DocsGen.Tests;

public sealed class CompatibilityTests
{
    [Theory]
    [InlineData("version")]
    [InlineData("response")]
    [InlineData("api-path")]
    [InlineData("manifest-path")]
    public void TerminalLfTokensAreRejectedWithoutNormalization(string token)
    {
        var api = JsonNode.Parse(Api("\"/items\":{\"get\":{\"responses\":{\"200\":{}}}}"))!;
        var manifest = JsonNode.Parse(Manifest(("GET", "/items")))!;
        switch (token)
        {
            case "version":
                api["openapi"] = "3.1.1\n";
                break;
            case "response":
                api["paths"]!["/items"]!["get"]!["responses"] = new JsonObject { ["200\n"] = new JsonObject() };
                break;
            case "api-path":
                api["paths"]!["/items\n"] = api["paths"]!["/items"]!.DeepClone();
                api["paths"]!.AsObject().Remove("/items");
                break;
            case "manifest-path":
                manifest["routes"]![0]!["path"] = "/items\n";
                break;
        }

        Should.Throw<InvalidDataException>(() => Compatibility.Compare(
            Inputs.Utf8.GetBytes(api.ToJsonString()), Inputs.Utf8.GetBytes(manifest.ToJsonString())));
    }

    [Fact]
    public void CountsDistinguishPartialStagedMissingAndExtensions()
    {
        var result = Compatibility.Compare(Api("""
            "/items": {"get":{"responses":{"200":{}}},"post":{"responses":{"200":{},"501":{}}}},
            "/later":{"get":{"responses":{"501":{}}}},
            "/v3/admin/grants":{"post":{"responses":{"501":{}}}},
            "/extra":{"get":{"responses":{"403":{}}}}
            """), Manifest(("GET", "/items"), ("POST", "/items"), ("GET", "/later"), ("GET", "/missing")));
        result["counts"]!.ToJsonString().ShouldBe(
            """{"actualOperations":5,"wireOperations":4,"declaredImplemented":1,"declaredPartial":1,"staged501":2,"declaredOther":1,"missing":1,"extensions":1,"unmatched":1}""");
        result["sdkConformance"]!.GetValue<string>().ShouldBe("not-run");
        result["authentication"]!.GetValue<string>().ShouldBe("not-assessed");
        result["routes"]![0]!["path"]!.GetValue<string>().ShouldBe("/extra");
    }

    [Fact]
    public void CaseAndParameterNamesAreNotNormalizedIntoCompatibility()
    {
        var api = Api("""
            "/Items/{local_id}":{"get":{"parameters":[{"name":"local_id","in":"path","required":true}],"responses":{"200":{}}}}
            """);
        var result = Compatibility.Compare(api, Manifest(("GET", "/items/{id}")));
        result["counts"]!["missing"]!.GetValue<int>().ShouldBe(1);
        result["counts"]!["unmatched"]!.GetValue<int>().ShouldBe(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{")]
    [InlineData("""{"openapi":"3.1.1","info":{"title":"fixture"},"components":{"schemas":{}},"paths":{}}""")]
    [InlineData("""{"openapi":"3.1.1","info":{"title":"fixture"},"components":{"schemas":{}},"paths":{"/items":{"get":{"responses":{}}}}}""")]
    [InlineData("""{"openapi":"3.1.1","info":{"title":"fixture"},"components":{"schemas":{}},"paths":{"/items":{"GET":{"responses":{"200":{}}}}}}""")]
    [InlineData("""{"openapi":"3.1.1","info":{"title":"fixture"},"components":{"schemas":{}},"paths":{"/items":{"get":{"responses":{"200":{}}},"get":{"responses":{"501":{}}}}}}""")]
    public void MalformedOrEmptyOpenApiFails(string api)
    {
        Should.Throw<Exception>(() => Compatibility.Compare(Inputs.Utf8.GetBytes(api), Manifest(("GET", "/items"))));
    }

    [Theory]
    [InlineData("version", "99.0")]
    [InlineData("sha256", "changed")]
    [InlineData("source", "https://untrusted.invalid")]
    public void DifferentWirePinFails(string key, string value)
    {
        var manifest = JsonNode.Parse(Manifest(("GET", "/items")))!;
        manifest[key] = value;
        Should.Throw<InvalidDataException>(() => Compatibility.Compare(
            Api("\"/items\":{\"get\":{\"responses\":{\"200\":{}}}}"), Inputs.Utf8.GetBytes(manifest.ToJsonString())));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("get")]
    public void DuplicateAndNonCanonicalManifestMethodsFail(string method)
    {
        Should.Throw<Exception>(() => Compatibility.Compare(Api("\"/items\":{\"get\":{\"responses\":{\"200\":{}}}}"),
            Manifest((method, "/items"), (method, "/items"))));
    }

    [Theory]
    [InlineData("wrong", true)]
    [InlineData("id", false)]
    public void InconsistentPathAliasOrOptionalPathParameterFails(string name, bool required)
    {
        var operation = new JsonObject
        {
            ["parameters"] = new JsonArray(new JsonObject { ["name"] = name, ["in"] = "path", ["required"] = required }),
            ["responses"] = new JsonObject { ["200"] = new JsonObject() },
        };
        var api = Api("\"/items/{id}\":{\"get\":" + operation.ToJsonString() + "}");
        Should.Throw<InvalidDataException>(() => Compatibility.Compare(api, Manifest(("GET", "/items/{id}"))));
    }

    [Fact]
    public void PathLevelParametersAreRespectedExactly()
    {
        var result = Compatibility.Compare(Api("""
            "/items/{id}":{"parameters":[{"name":"id","in":"path","required":true}],"get":{"responses":{"200":{}}}}
            """), Manifest(("GET", "/items/{id}")));
        result["counts"]!["missing"]!.GetValue<int>().ShouldBe(0);
    }

    [Theory]
    [InlineData("3.invalid", "{}")]
    [InlineData("3.1.1", "null")]
    [InlineData("3.1.1", "\"not-a-response\"")]
    public void InvalidVersionOrResponseObjectCannotClaimImplementation(string version, string response)
    {
        var api = Inputs.Utf8.GetBytes(
            "{\"openapi\":\"" + version + "\",\"info\":{\"title\":\"fixture\"},\"components\":{\"schemas\":{}}," +
            "\"paths\":{\"/items\":{\"get\":{\"responses\":{\"200\":" + response + "}}}}}");
        Should.Throw<InvalidDataException>(() => Compatibility.Compare(api, Manifest(("GET", "/items"))));
    }

    internal static byte[] Api(string paths) => Inputs.Utf8.GetBytes(
        """{"openapi":"3.1.1","info":{"title":"First-party test fixture"},"components":{"schemas":{}},"paths":{""" + paths + "}}");

    internal static byte[] Manifest(params (string Method, string Path)[] routes) => Inputs.Utf8.GetBytes(new JsonObject
    {
        ["source"] = "https://docs.honcho.dev/v3/openapi.json",
        ["sha256"] = "6aa5dd7fa958719e5f12565592617621141f0b9ae0a6b5450802dc152d2e3fe9",
        ["version"] = "3.3.0",
        ["routes"] = new JsonArray(routes.Select(route => (JsonNode)new JsonObject
        {
            ["method"] = route.Method, ["path"] = route.Path, ["query"] = new JsonArray(), ["statuses"] = new JsonArray(200),
        }).ToArray()),
    }.ToJsonString());
}
