using System.Text.Json.Nodes;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class PnpmBuildMetadataTests
{
    [Theory]
    [InlineData("1.0.0+build..x", false, false)]
    [InlineData("1.0.0+.", false, false)]
    [InlineData("1.0.0+..", true, false)]
    [InlineData("1.0.0+build.001-x", false, true)]
    [InlineData("1.0.0+build.001-x", true, true)]
    [InlineData("1.0.0+BUILD.A-9", false, true)]
    [InlineData("1.0.0+build.", true, false)]
    [InlineData("1.0.0+.build", false, false)]
    [InlineData("1.0.0+", true, false)]
    [InlineData("1.0.0+b\u00fcild", false, false)]
    [InlineData("1.0.0+build || 1.0.0+bad..x", false, false)]
    public async Task PN3_BuildIdentifiersAreValidatedBeforePrecedence(string token, bool exact, bool valid)
    {
        using var fixture = new PnpmRepairFixture();
        if (exact)
        {
            fixture.Rewrite("leaf", metadata => metadata["version"] = token);
            PnpmRepairFixture.Rename(fixture.Packages, "leaf@1.0.0", "leaf@" + token);
            PnpmRepairFixture.Rename(fixture.Snapshots, "leaf@1.0.0", "leaf@" + token);
            fixture.Snapshots[PnpmRepairFixture.Consumer]!["dependencies"]!["leaf"] = token;
        }
        else fixture.Rewrite("consumer", metadata => metadata["dependencies"] = new JsonObject { ["leaf"] = token });
        var result = await fixture.Run();
        result.Exit.ShouldBe(valid ? 0 : 1, result.Output);
    }
}
