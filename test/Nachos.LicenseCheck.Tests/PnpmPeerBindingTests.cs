using System.Text.Json.Nodes;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class PnpmPeerBindingTests
{
    [Theory]
    [InlineData("extra-ordinary-dependency", false)]
    [InlineData("optional-context-without-edge", false)]
    [InlineData("optional-fully-absent", true)]
    [InlineData("optional-fully-present", true)]
    [InlineData("optional-in-optional-edges", true)]
    [InlineData("optional-edge-without-context", false)]
    [InlineData("optional-range-mismatch", false)]
    [InlineData("optional-overlapping-required", false)]
    [InlineData("direct", true)]
    public async Task PN2_EachDirectPeerContextAgreesWithItsBinding(string form, bool valid)
    {
        using var fixture = new PnpmRepairFixture();
        var key = PnpmRepairFixture.Consumer;
        if (form.StartsWith("optional", StringComparison.Ordinal))
        {
            fixture.Rewrite("consumer", metadata => metadata["peerDependenciesMeta"] = JsonNode.Parse("""{"peer":{"optional":true}}"""));
            fixture.Packages["consumer@1.0.0"]!["peerDependenciesMeta"] = JsonNode.Parse("""{"peer":{"optional":"true"}}""");
            if (form is "optional-in-optional-edges" or "optional-edge-without-context" or "optional-range-mismatch")
                fixture.Snapshots[key]!["optionalDependencies"]!["peer"] = "2.0.0";
            if (form == "optional-range-mismatch")
            {
                fixture.Rewrite("consumer", metadata => metadata["peerDependencies"]!["peer"] = "^3.0.0");
                fixture.Packages["consumer@1.0.0"]!["peerDependencies"]!["peer"] = "^3.0.0";
            }
            if (form == "optional-overlapping-required")
                fixture.Rewrite("consumer", metadata => metadata["dependencies"]!["peer"] = "^2.0.0");
            if (form != "optional-fully-present") fixture.Snapshots[key]!["dependencies"]!.AsObject().Remove("peer");
        }
        var changed = form == "extra-ordinary-dependency" ? key + "(leaf@1.0.0)"
            : form is "optional-fully-absent" or "optional-edge-without-context" or "optional-overlapping-required" ? "consumer@1.0.0" : key;
        if (changed != key)
        {
            PnpmRepairFixture.Rename(fixture.Snapshots, key, changed);
            fixture.Documents[1]["importers"]!["."]!["dependencies"]!["consumer"]!["version"] = changed["consumer@".Length..];
        }
        var result = await fixture.Run();
        result.Exit.ShouldBe(valid ? 0 : 1, result.Output);
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("unexplained-context", false)]
    [InlineData("unexplained-transitive-name", false)]
    [InlineData("missing-forwarded-context", false)]
    [InlineData("unresolved-optional", true)]
    [InlineData("locally-provided", true)]
    [InlineData("self-justifying-cycle", false)]
    public async Task PN2_TransitiveContextRequiresAMatchingChildPeer(string form, bool valid)
    {
        using var fixture = new PnpmRepairFixture();
        fixture.Rewrite("consumer", metadata => metadata["peerDependencies"] = new JsonObject());
        fixture.Packages["consumer@1.0.0"]!["peerDependencies"] = new JsonObject();
        fixture.Rewrite("leaf", metadata => metadata["peerDependencies"] = JsonNode.Parse("""{"peer":"^2.0.0"}"""));
        fixture.Packages["leaf@1.0.0"]!["peerDependencies"] = JsonNode.Parse("""{"peer":"^2.0.0"}""");
        PnpmRepairFixture.Rename(fixture.Snapshots, "leaf@1.0.0", "leaf@1.0.0(peer@2.0.0)");
        fixture.Snapshots["leaf@1.0.0(peer@2.0.0)"]!["dependencies"] = JsonNode.Parse("""{"peer":"2.0.0"}""");
        var snapshot = fixture.Snapshots[PnpmRepairFixture.Consumer]!;
        snapshot["dependencies"]!.AsObject().Remove("peer");
        snapshot["dependencies"]!["leaf"] = "1.0.0(peer@2.0.0)";
        if (form != "unexplained-context") snapshot["transitivePeerDependencies"] = new JsonArray("peer");
        if (form == "unexplained-transitive-name") snapshot["transitivePeerDependencies"] = new JsonArray("peer", "absent");
        if (form == "unresolved-optional")
        {
            fixture.Rewrite("leaf", metadata => metadata["peerDependenciesMeta"] = JsonNode.Parse("""{"peer":{"optional":true}}"""));
            fixture.Packages["leaf@1.0.0"]!["peerDependenciesMeta"] = JsonNode.Parse("""{"peer":{"optional":"true"}}""");
            PnpmRepairFixture.Rename(fixture.Snapshots, "leaf@1.0.0(peer@2.0.0)", "leaf@1.0.0");
            fixture.Snapshots["leaf@1.0.0"]!["dependencies"] = new JsonObject();
            snapshot["dependencies"]!["leaf"] = "1.0.0";
        }
        if (form == "locally-provided")
        {
            fixture.Rewrite("consumer", metadata => metadata["dependencies"]!["peer"] = "^2.0.0");
            snapshot["dependencies"]!["peer"] = "2.0.0";
            snapshot.AsObject().Remove("transitivePeerDependencies");
        }
        if (form == "self-justifying-cycle")
        {
            fixture.Rewrite("leaf", metadata =>
            {
                metadata["peerDependencies"] = new JsonObject();
                metadata["dependencies"] = JsonNode.Parse("""{"consumer":"1.0.0"}""");
            });
            fixture.Packages["leaf@1.0.0"]!["peerDependencies"] = new JsonObject();
            var child = fixture.Snapshots["leaf@1.0.0(peer@2.0.0)"]!;
            child["dependencies"] = JsonNode.Parse("""{"consumer":"1.0.0(peer@2.0.0)"}""");
            child["transitivePeerDependencies"] = new JsonArray("peer");
        }
        if (form is "missing-forwarded-context" or "unresolved-optional" or "locally-provided")
        {
            PnpmRepairFixture.Rename(fixture.Snapshots, PnpmRepairFixture.Consumer, "consumer@1.0.0");
            fixture.Documents[1]["importers"]!["."]!["dependencies"]!["consumer"]!["version"] = "1.0.0";
        }
        var result = await fixture.Run();
        result.Exit.ShouldBe(valid ? 0 : 1, result.Output);
    }
}
