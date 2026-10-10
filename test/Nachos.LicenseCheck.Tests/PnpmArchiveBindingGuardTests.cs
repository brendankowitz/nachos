using System.Text.Json.Nodes;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class PnpmArchiveBindingGuardTests
{
    [Theory]
    [InlineData("a", true)]
    [InlineData("b", false)]
    public void ArchiveIdentityIncludesExactBuildMetadata(string archiveBuild, bool valid)
    {
        using var fixture = new PnpmRepairFixture();
        fixture.Rewrite("leaf", metadata => metadata["version"] = "1.0.0+" + archiveBuild);
        PnpmRepairFixture.Rename(fixture.Packages, "leaf@1.0.0", "leaf@1.0.0+a");
        PnpmRepairFixture.Rename(fixture.Snapshots, "leaf@1.0.0", "leaf@1.0.0+a");
        fixture.Snapshots[PnpmRepairFixture.Consumer]!["dependencies"]!["leaf"] = "1.0.0+a";
        fixture.Save();
        var errors = fixture.Audit.Check().Errors;
        if (valid) errors.ShouldBeEmpty();
        else errors.ShouldContain(error => error.Contains("leaf@1.0.0+a: pnpm archive identity disagrees with lock", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("os", "linux", "linux", true)]
    [InlineData("os", "linux", "darwin", false)]
    [InlineData("cpu", "x64", "x64", true)]
    [InlineData("cpu", "x64", "arm64", false)]
    [InlineData("libc", "glibc", "glibc", true)]
    [InlineData("libc", "glibc", "musl", false)]
    public void ArchivePlatformMustAgreeWithLock(string field, string archiveValue, string lockValue, bool valid)
    {
        using var fixture = new PnpmRepairFixture();
        fixture.Rewrite("leaf", metadata => metadata[field] = new JsonArray(archiveValue));
        fixture.Packages["leaf@1.0.0"]![field] = new JsonArray(lockValue);
        fixture.Save();
        var errors = fixture.Audit.Check().Errors;
        if (valid) errors.ShouldBeEmpty();
        else errors.ShouldContain(error => error.Contains("leaf@1.0.0: pnpm platform declaration disagrees: " + field, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChildPeerContextMustAgreeWithLocalProvider(bool conflict)
    {
        using var fixture = new PnpmRepairFixture();
        var version = conflict ? "2.1.0" : "2.0.0";
        if (conflict)
        {
            var original = fixture.Packages["peer@2.0.0"]!.DeepClone();
            fixture.Rewrite("peer", metadata => metadata["version"] = version);
            fixture.Packages["peer@2.1.0"] = fixture.Packages["peer@2.0.0"]!.DeepClone();
            fixture.Packages["peer@2.0.0"] = original;
            fixture.Snapshots["peer@2.1.0"] = new JsonObject();
        }
        fixture.Rewrite("leaf", metadata => metadata["peerDependencies"] = JsonNode.Parse("""{"peer":"^2.0.0"}"""));
        fixture.Packages["leaf@1.0.0"]!["peerDependencies"] = JsonNode.Parse("""{"peer":"^2.0.0"}""");
        var leaf = "leaf@1.0.0(peer@" + version + ")";
        PnpmRepairFixture.Rename(fixture.Snapshots, "leaf@1.0.0", leaf);
        fixture.Snapshots[leaf]!["dependencies"] = new JsonObject { ["peer"] = version };
        fixture.Snapshots[PnpmRepairFixture.Consumer]!["dependencies"]!["leaf"] = leaf["leaf@".Length..];
        fixture.Save();
        var errors = fixture.Audit.Check().Errors;
        if (!conflict) errors.ShouldBeEmpty();
        else errors.ShouldContain(error => error.Contains(
            "pnpm child peer disagrees with local provider: consumer@1.0.0(peer@2.0.0)/peer", StringComparison.Ordinal));
    }
}
