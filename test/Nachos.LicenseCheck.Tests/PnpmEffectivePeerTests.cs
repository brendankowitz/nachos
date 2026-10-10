using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class PnpmEffectivePeerTests
{
    [Theory]
    [InlineData("optional", true)]
    [InlineData("implicit-optional", true)]
    [InlineData("resolved", true)]
    [InlineData("removed-clean", true)]
    [InlineData("removed-implicit-clean", true)]
    [InlineData("removed-stale", false)]
    [InlineData("removed-stale-root", false)]
    [InlineData("removed-stale-leaf", false)]
    [InlineData("removed-edge", false)]
    [InlineData("missing-edge", false)]
    [InlineData("missing-context", false)]
    [InlineData("conflicting-bindings", false)]
    public async Task PN2_R1_ChainsPropagateOnlyEffectiveExternalRequirements(string form, bool valid)
    {
        using var fixture = new PnpmRepairFixture();
        var resolved = form is "resolved" or "removed-edge" or "missing-edge" or "missing-context" or "conflicting-bindings";
        var keys = Chain(fixture, resolved);
        if (form.Contains("implicit", StringComparison.Ordinal))
        {
            fixture.Rewrite("foreign", metadata => metadata.Remove("peerDependencies"));
            fixture.Packages["foreign@1.0.0"]!["peerDependencies"]!["peer"] = "*";
        }
        if (form.StartsWith("removed", StringComparison.Ordinal))
        {
            fixture.Documents[1]["overrides"] = JsonNode.Parse("""{"foreign@1.0.0>peer":"-"}""");
            fixture.Audit.WriteText("web/pnpm-workspace.yaml", "overrides:\n  'foreign@1.0.0>peer': '-'\n");
            if (form is "removed-clean" or "removed-implicit-clean" or "removed-stale-leaf")
                fixture.Snapshots[keys.Consumer]!.AsObject().Remove("transitivePeerDependencies");
            if (form is "removed-clean" or "removed-implicit-clean" or "removed-stale-root")
                fixture.Snapshots[keys.Leaf]!.AsObject().Remove("transitivePeerDependencies");
        }
        if (form == "missing-edge") fixture.Snapshots[keys.Foreign]!["dependencies"]!.AsObject().Remove("peer");
        if (form == "missing-context")
        {
            PnpmRepairFixture.Rename(fixture.Snapshots, keys.Foreign, "foreign@1.0.0");
            fixture.Snapshots[keys.Leaf]!["dependencies"]!["foreign"] = "1.0.0";
            fixture.Snapshots[keys.Consumer]!["optionalDependencies"]!["foreign"] = "1.0.0";
        }
        if (form == "conflicting-bindings")
        {
            var original = fixture.Packages["peer@2.0.0"]!.DeepClone();
            fixture.Rewrite("peer", metadata => metadata["version"] = "2.1.0");
            fixture.Packages["peer@2.1.0"] = fixture.Packages["peer@2.0.0"]!.DeepClone();
            fixture.Packages["peer@2.0.0"] = original;
            fixture.Snapshots["peer@2.1.0"] = new JsonObject();
            var alternate = fixture.Snapshots[keys.Foreign]!.DeepClone();
            alternate["dependencies"]!["peer"] = "2.1.0";
            fixture.Snapshots["foreign@1.0.0(peer@2.1.0)"] = alternate;
            fixture.Snapshots[keys.Consumer]!["optionalDependencies"]!["foreign"] = "1.0.0(peer@2.1.0)";
        }
        var result = await fixture.Run();
        result.Exit.ShouldBe(valid ? 0 : 1, result.Output);
    }

    [Theory]
    [InlineData("dependencies", false)]
    [InlineData("dependencies", true)]
    [InlineData("optionalDependencies", false)]
    [InlineData("optionalDependencies", true)]
    public async Task PN2_R1_IdenticalArchiveDeclaredLocalProviderWorksAsChildAndRoot(string field, bool childFirst)
    {
        using var fixture = new PnpmRepairFixture();
        LocalProvider(fixture, field);
        if (childFirst)
        {
            var definitions = fixture.Packages.ToArray();
            fixture.Packages.Clear();
            foreach (var pair in definitions.Reverse()) fixture.Packages.Add(pair.Key, pair.Value);
        }
        var record = fixture.Packages["leaf@1.0.0"]!.ToJsonString();
        var snapshot = fixture.Snapshots["leaf@1.0.0"]!.ToJsonString();
        var integrity = fixture.Packages["leaf@1.0.0"]!["resolution"]!["integrity"]!.GetValue<string>();
        var digest = Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(fixture.Base.Archives["leaf"])));
        integrity.ShouldBe("sha512-" + digest);
        var child = await fixture.Run();
        var childReport = File.ReadAllText(fixture.Audit.Full("repair-report.json"));

        fixture.Packages.Remove("consumer@1.0.0");
        fixture.Snapshots.Remove("consumer@1.0.0");
        var imports = fixture.Documents[1]["importers"]!["."]!["dependencies"]!.AsObject();
        imports.Remove("consumer");
        imports["leaf"] = JsonNode.Parse("""{"specifier":"1.0.0","version":"1.0.0"}""");
        var manifest = JsonNode.Parse(File.ReadAllText(fixture.Audit.Full("web/package.json")))!;
        manifest["dependencies"]!.AsObject().Remove("consumer");
        manifest["dependencies"]!["leaf"] = "1.0.0";
        fixture.Audit.WriteText("web/package.json", manifest.ToJsonString());
        var root = await fixture.Run();

        fixture.Packages["leaf@1.0.0"]!.ToJsonString().ShouldBe(record);
        fixture.Snapshots["leaf@1.0.0"]!.ToJsonString().ShouldBe(snapshot);
        Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(fixture.Base.Archives["leaf"]))).ShouldBe(digest);
        root.Exit.ShouldBe(0, root.Output);
        child.Exit.ShouldBe(0, child.Output + childReport);
    }

    [Theory]
    [InlineData("dependencies")]
    [InlineData("optionalDependencies")]
    public async Task PN2_R1_LocallySatisfiedChildCannotJustifyStaleUpstreamAnnotations(string field)
    {
        using var fixture = new PnpmRepairFixture();
        LocalProvider(fixture, field);
        fixture.Snapshots["consumer@1.0.0"]!["transitivePeerDependencies"] = new JsonArray("peer");
        var result = await fixture.Run();
        result.Exit.ShouldBe(1, result.Output);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("identity")]
    public async Task PN2_R1_ParentCannotUseUnavailableOrMisboundChildMetadata(string form)
    {
        using var fixture = new PnpmRepairFixture();
        if (form == "missing") File.Delete(fixture.Base.Archives["leaf"]);
        else fixture.Rewrite("leaf", metadata => metadata["name"] = "not-leaf");
        var result = await fixture.Run();
        result.Exit.ShouldBe(1, result.Output);
        var report = JsonNode.Parse(File.ReadAllText(fixture.Audit.Full("repair-report.json")))!;
        report["packages"]!.AsArray().Select(package => package!["package"]!.GetValue<string>())
            .ShouldNotContain("consumer", result.Output);
    }

    private static void LocalProvider(PnpmRepairFixture fixture, string field)
    {
        fixture.Rewrite("consumer", metadata => metadata["peerDependencies"] = new JsonObject());
        fixture.Packages["consumer@1.0.0"]!["peerDependencies"] = new JsonObject();
        fixture.Snapshots[PnpmRepairFixture.Consumer]!["dependencies"]!.AsObject().Remove("peer");
        SetConsumer(fixture, "consumer@1.0.0");
        fixture.Rewrite("leaf", metadata =>
        {
            metadata["peerDependencies"] = JsonNode.Parse("""{"peer":"^2.0.0"}""");
            metadata[field] = JsonNode.Parse("""{"peer":"^2.0.0"}""");
        });
        fixture.Packages["leaf@1.0.0"]!["peerDependencies"] = JsonNode.Parse("""{"peer":"^2.0.0"}""");
        fixture.Snapshots["leaf@1.0.0"]![field] = JsonNode.Parse("""{"peer":"2.0.0"}""");
    }

    private static (string Consumer, string Leaf, string Foreign) Chain(PnpmRepairFixture fixture, bool resolved)
    {
        fixture.Rewrite("consumer", metadata => metadata["peerDependencies"] = new JsonObject());
        fixture.Packages["consumer@1.0.0"]!["peerDependencies"] = new JsonObject();
        fixture.Rewrite("leaf", metadata => metadata["dependencies"] = JsonNode.Parse("""{"foreign":"1.0.0"}"""));
        fixture.Rewrite("foreign", metadata =>
        {
            metadata["peerDependencies"] = JsonNode.Parse("""{"peer":"^2.0.0"}""");
            if (!resolved) metadata["peerDependenciesMeta"] = JsonNode.Parse("""{"peer":{"optional":true}}""");
        });
        fixture.Packages["foreign@1.0.0"]!["peerDependencies"] = JsonNode.Parse("""{"peer":"^2.0.0"}""");
        if (!resolved)
            fixture.Packages["foreign@1.0.0"]!["peerDependenciesMeta"] = JsonNode.Parse("""{"peer":{"optional":"true"}}""");
        var foreign = resolved ? "foreign@1.0.0(peer@2.0.0)" : "foreign@1.0.0";
        var leaf = resolved ? "leaf@1.0.0(peer@2.0.0)" : "leaf@1.0.0";
        if (resolved)
        {
            PnpmRepairFixture.Rename(fixture.Snapshots, "foreign@1.0.0", foreign);
            PnpmRepairFixture.Rename(fixture.Snapshots, "leaf@1.0.0", leaf);
        }
        fixture.Snapshots[foreign]!["dependencies"] = resolved ? JsonNode.Parse("""{"peer":"2.0.0"}""") : new JsonObject();
        fixture.Snapshots[leaf]!["dependencies"] = new JsonObject { ["foreign"] = foreign["foreign@".Length..] };
        fixture.Snapshots[leaf]!["transitivePeerDependencies"] = new JsonArray("peer");
        var consumer = fixture.Snapshots[PnpmRepairFixture.Consumer]!;
        consumer["dependencies"] = new JsonObject { ["leaf"] = leaf["leaf@".Length..] };
        consumer["optionalDependencies"]!["foreign"] = foreign["foreign@".Length..];
        consumer["transitivePeerDependencies"] = new JsonArray("peer");
        var consumerKey = resolved ? PnpmRepairFixture.Consumer : "consumer@1.0.0";
        if (!resolved) SetConsumer(fixture, consumerKey);
        return (consumerKey, leaf, foreign);
    }

    private static void SetConsumer(PnpmRepairFixture fixture, string key)
    {
        PnpmRepairFixture.Rename(fixture.Snapshots, PnpmRepairFixture.Consumer, key);
        fixture.Documents[1]["importers"]!["."]!["dependencies"]!["consumer"]!["version"] = key["consumer@".Length..];
    }
}
