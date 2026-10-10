using System.Text;
using System.Text.Json;
using Nachos.Core.Idempotency;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class RequestHasherTests
{
    private const string Route = "/v3/workspaces/{w}/sessions/{s}/messages";

    [Fact]
    public void Hash_IsDeterministicLowercaseSha256()
    {
        var values = new Dictionary<string, string> { ["w"] = "Workspace", ["s"] = "Session" };
        var body = Encoding.UTF8.GetBytes("""{"messages":[]}""");

        var hash = RequestHasher.Hash("POST", Route, values, body);

        hash.Length.ShouldBe(64);
        hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f').ShouldBeTrue();
        RequestHasher.Hash("POST", Route, values, body).ShouldBe(hash);
        RequestHasher.Hash("PUT", Route, values, body).ShouldNotBe(hash);
        RequestHasher.Hash("POST", Route, values, "{}"u8).ShouldNotBe(hash);
    }

    [Fact]
    public void DictionaryInsertionOrderDoesNotChangeRequestIdentity()
    {
        var first = new Dictionary<string, string> { ["w"] = "Workspace", ["s"] = "Session" };
        var reordered = new Dictionary<string, string> { ["s"] = "Session", ["w"] = "Workspace" };

        RequestHasher.Hash("POST", Route, first, "{}"u8)
            .ShouldBe(RequestHasher.Hash("POST", Route, reordered, "{}"u8));
    }

    [Fact]
    public void MessageTrailingSlashAliasHasOneCanonicalTarget()
    {
        var values = new Dictionary<string, string> { ["w"] = "W", ["s"] = "S" };

        RequestHasher.Hash("POST", Route, values, "{}"u8)
            .ShouldBe(RequestHasher.Hash("POST", Route + "/", values, "{}"u8));
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("session")]
    [InlineData("workspace-case")]
    [InlineData("session-case")]
    [InlineData("route")]
    public void CanonicalTargetPreservesScopeAndCase(string difference)
    {
        var values = new Dictionary<string, string> { ["w"] = "W", ["s"] = "S" };
        var original = RequestHasher.Hash("POST", Route, values, "{}"u8);
        var route = Route;
        switch (difference)
        {
            case "workspace": values["w"] = "Other"; break;
            case "session": values["s"] = "Other"; break;
            case "workspace-case": values["w"] = "w"; break;
            case "session-case": values["s"] = "s"; break;
            case "route": route += "/list"; break;
        }

        RequestHasher.Hash("POST", route, values, "{}"u8).ShouldNotBe(original);
    }

    [Fact]
    public void ComponentBoundariesCannotCollideByConcatenation()
    {
        var empty = new Dictionary<string, string>();
        RequestHasher.Hash("AB", "C", empty, "{}"u8)
            .ShouldNotBe(RequestHasher.Hash("A", "BC", empty, "{}"u8));
        RequestHasher.Hash("POST", Route, new Dictionary<string, string> { ["a"] = "bc" }, "{}"u8)
            .ShouldNotBe(RequestHasher.Hash("POST", Route, new Dictionary<string, string> { ["ab"] = "c" }, "{}"u8));
    }

    [Theory]
    [InlineData("""{"messages":[]}""", """{"messages":[],"unknown":null}""")]
    [InlineData("""{"configuration":null}""", "{}")]
    [InlineData("""{"messages":[{"configuration":{"reasoning":{"enabled":true}}}]}""",
        """{"messages":[{"configuration":{"reasoning":{"enabled":false}}}]}""")]
    [InlineData("""{"a":[1,2]}""", """{"a":[2,1]}""")]
    public void CompleteRawBodyContributesToRequestIdentity(string original, string changed)
    {
        using var first = JsonDocument.Parse(original);
        using var second = JsonDocument.Parse(changed);
        var values = new Dictionary<string, string> { ["w"] = "W", ["s"] = "S" };

        RequestHasher.Hash("POST", Route, values, CanonicalJson.Serialize(first.RootElement))
            .ShouldNotBe(RequestHasher.Hash("POST", Route, values, CanonicalJson.Serialize(second.RootElement)));
    }
}
