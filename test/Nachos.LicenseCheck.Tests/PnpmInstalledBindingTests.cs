using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class PnpmInstalledBindingTests
{
    [Theory]
    [InlineData("correct", true)]
    [InlineData("substituted-physical-slot", false)]
    [InlineData("wrong-linked-identity", false)]
    [InlineData("nonpackage-target", false)]
    [InlineData("hidden-unlocked-gpl", false)]
    [InlineData("hoisted-alias", true)]
    [InlineData("unknown-context", false)]
    [InlineData("changed-linked-evidence", false)]
    public async Task PN1_InstalledBindingIsProvedRatherThanAssumed(string form, bool valid)
    {
        using var fixture = new PnpmRepairFixture();
        var target = fixture.Install(form == "wrong-linked-identity" ? "leaf@1.0.0" : "peer@2.0.0",
            form == "wrong-linked-identity" ? "leaf" : "peer",
            form is "wrong-linked-identity" or "substituted-physical-slot" ? "leaf" : "peer");
        if (form == "nonpackage-target")
        {
            target = Path.Combine(target, "not-a-package");
            Directory.CreateDirectory(target);
        }
        if (form == "hidden-unlocked-gpl")
        {
            target = fixture.Audit.Full("web/node_modules/.pnpm/node_modules/unlocked");
            fixture.Audit.Write("web/node_modules/.pnpm/node_modules/unlocked/package.json",
                new { name = "unlocked", version = "1.0.0", license = "GPL-3.0-only" });
            // Complete MIT text must not conceal an unrecorded GPL declaration.
            fixture.Audit.WriteText("web/node_modules/.pnpm/node_modules/unlocked/LICENSE",
                AuditFixture.Mit);
        }
        if (form == "hoisted-alias")
        {
            fixture.Link("", "peer", target);
            target = fixture.Audit.Full("web/node_modules/.pnpm/node_modules/peer");
        }
        if (form == "changed-linked-evidence") File.WriteAllText(Path.Combine(target, "LICENSE"), "Changed terms.");
        if (form != "substituted-physical-slot")
            fixture.Link(form == "unknown-context" ? "unbound-context" : "consumer@1.0.0_peer@2.0.0", "peer", target);
        var result = await fixture.Run();
        result.Exit.ShouldBe(valid ? 0 : 1, result.Output);
    }

    [Theory]
    [InlineData("correct")]
    [InlineData("wrong-target")]
    [InlineData("wrong-hash")]
    public async Task PN1_ScopedHashedContextRetainsExactBindings(string form)
    {
        using var fixture = new PnpmRepairFixture();
        const string name = "@scope/consumer-with-a-name-long-enough-to-require-hashing";
        fixture.Rewrite("consumer", metadata => metadata["name"] = name);
        PnpmRepairFixture.Rename(fixture.Packages, "consumer@1.0.0", name + "@1.0.0");
        var key = name + "@1.0.0(peer@2.0.0)";
        PnpmRepairFixture.Rename(fixture.Snapshots, PnpmRepairFixture.Consumer, key);
        PnpmRepairFixture.Rename(fixture.Documents[1]["importers"]!["."]!["dependencies"]!.AsObject(), "consumer", name);
        var manifest = JsonNode.Parse(File.ReadAllText(fixture.Audit.Full("web/package.json")))!;
        PnpmRepairFixture.Rename(manifest["dependencies"]!.AsObject(), "consumer", name);
        fixture.Audit.WriteText("web/package.json", manifest.ToJsonString());
        var encoded = key[..^1].Replace('/', '+').Replace('(', '_');
        var folder = encoded[..27] + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(encoded)))[..32].ToLowerInvariant();
        if (form == "wrong-hash") folder = folder[..^1] + (folder[^1] == '0' ? '1' : '0');
        fixture.Install(folder, name, "consumer");
        var wrongTarget = form == "wrong-target";
        var target = fixture.Install(wrongTarget ? "leaf@1.0.0" : "peer@2.0.0", wrongTarget ? "leaf" : "peer", wrongTarget ? "leaf" : "peer");
        fixture.Link(folder, "peer", target);
        var result = await fixture.Run();
        result.Exit.ShouldBe(form == "correct" ? 0 : 1, result.Output);
    }

    [Fact]
    public async Task PN1_SameVersionCannotSubstituteAnotherPeerContext()
    {
        using var fixture = new PnpmRepairFixture();
        var priorPeer = fixture.Packages["peer@2.0.0"]!.DeepClone();
        fixture.Rewrite("peer", metadata => metadata["version"] = "2.1.0");
        fixture.Packages["peer@2.1.0"] = fixture.Packages["peer@2.0.0"]!.DeepClone();
        fixture.Packages["peer@2.0.0"] = priorPeer;
        fixture.Snapshots["peer@2.1.0"] = new JsonObject();
        var alternate = fixture.Snapshots[PnpmRepairFixture.Consumer]!.DeepClone();
        alternate["dependencies"]!["peer"] = "2.1.0";
        fixture.Snapshots["consumer@1.0.0(peer@2.1.0)"] = alternate;
        var target = fixture.Install("consumer@1.0.0_peer@2.1.0", "consumer", "consumer");
        fixture.Link("consumer@1.0.0_peer@2.0.0", "consumer", target);
        var result = await fixture.Run();
        result.Exit.ShouldBe(1, result.Output);
    }
}
