using System.Diagnostics;
using System.Text.Json.Nodes;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class RoundTwoRegressionTests
{
    [Theory]
    [InlineData("COPYING.GPL", "GNU GENERAL PUBLIC LICENSE Version 3")]
    [InlineData("LICENSE.custom", "Commercial redistribution prohibited.")]
    [InlineData("licenses/additional.custom", "Commercial redistribution prohibited.")]
    public void R1_UnsupportedInstalledDocument_CannotDisappear(string path, string text)
    {
        using var fixture = new AuditFixture();
        fixture.WriteText(".github/scripts/node_modules/baseline/" + path, text);
        fixture.Check().Errors.ShouldContain(error => error.Contains("unsupported license entry", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("COPYING.GPL", "GNU GENERAL PUBLIC LICENSE Version 3")]
    [InlineData("LICENSE.custom", "Commercial redistribution prohibited.")]
    [InlineData("licenses/additional.custom", "Commercial redistribution prohibited.")]
    public void R1_UnsupportedArchiveDocument_CannotDisappear(string path, string text)
    {
        using var fixture = new AuditFixture();
        NpmReviewTests.Archive(fixture, extraPath: "package/" + path, extraText: text);
        fixture.Check().Errors.ShouldContain(error => error.Contains("unsupported license entry", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void R1_SupportedProhibitedNotice_StillFails(bool archive)
    {
        using var fixture = new AuditFixture();
        if (archive)
        {
            NpmReviewTests.Archive(fixture, extraPath: "package/THIRD-PARTY-NOTICES.txt", extraText: AuditFixture.Gpl);
        }
        else
        {
            fixture.WriteText(".github/scripts/node_modules/baseline/THIRD-PARTY-NOTICES.txt", AuditFixture.Gpl);
        }
        fixture.Check().Errors.ShouldContain(error => error.Contains("prohibited", StringComparison.Ordinal));
    }

    [Fact]
    public void R2_OrdinaryPackageLocalEvidenceDirectory_Passes()
    {
        using var fixture = new AuditFixture();
        File.Delete(fixture.Full(".github/scripts/node_modules/baseline/LICENSE"));
        fixture.WriteText(".github/scripts/node_modules/baseline/licenses/subdirectory/LICENSE", AuditFixture.Mit);
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("licenses")]
    [InlineData("licenses/nested")]
    public void R2_LinkedDirectory_IsRejectedBeforeTraversal(string relative)
    {
        using var fixture = new AuditFixture();
        using var outside = new AuditFixture();
        File.Delete(fixture.Full(".github/scripts/node_modules/baseline/LICENSE"));
        outside.WriteText("external/LICENSE", AuditFixture.Mit);
        var link = fixture.Full(".github/scripts/node_modules/baseline/" + relative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        CreateDirectoryLink(link, outside.Full("external"));
        try
        {
            (File.GetAttributes(link) & FileAttributes.ReparsePoint).ShouldBe(FileAttributes.ReparsePoint);
            fixture.Check().Errors.ShouldContain(error => error.Contains("Linked evidence path", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Theory]
    [InlineData("LICENSE")]
    [InlineData("licenses/LICENSE")]
    [InlineData("package.json")]
    public void R2_LinkedFile_IsRejectedBeforeReading(string relative)
    {
        using var fixture = new AuditFixture();
        using var outside = new AuditFixture();
        var link = fixture.Full(".github/scripts/node_modules/baseline/" + relative);
        var original = fixture.Full(".github/scripts/node_modules/baseline/LICENSE");
        var text = relative == "package.json"
            ? """{"name":"baseline","version":"1.0.0","license":"MIT"}""" : AuditFixture.Mit;
        outside.WriteText("external-evidence", text);
        if (relative != "package.json")
        {
            File.Delete(original);
        }
        if (File.Exists(link))
        {
            File.Delete(link);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, outside.Full("external-evidence"));
        try
        {
            (File.GetAttributes(link) & FileAttributes.ReparsePoint).ShouldBe(FileAttributes.ReparsePoint);
            fixture.Check().Errors.ShouldContain(error => error.Contains("Linked evidence path", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(link);
        }
    }

    [Theory]
    [InlineData(null, "1.0.0", true)]
    [InlineData("ancestor@1.0.0", "1.1.0", true)]
    [InlineData(null, "1.1.0", false)]
    [InlineData("ancestor@9.0.0", "1.1.0", false)]
    [InlineData("ancestor@1.0.0", "1.1.1", false)]
    public void R3_ExactVersionUsesApplicableLogicalOverride(string? ancestor, string resolved, bool accepted)
    {
        using var fixture = OverrideGraph(resolved);
        if (ancestor is not null)
        {
            Overrides(fixture, new JsonObject { [ancestor] = new JsonObject { ["middle@2.0.0"] = new JsonObject { ["leaf"] = "1.1.0" } } });
        }
        var report = fixture.Check();
        if (accepted)
        {
            report.Errors.ShouldBeEmpty();
        }
        else
        {
            report.Errors.ShouldContain(error => error.Contains("exact dependency", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void R3_OverrideCannotHideMissingRequiredRecord()
    {
        using var fixture = OverrideGraph("1.1.0");
        Overrides(fixture, new JsonObject { ["leaf"] = "1.1.0" });
        EditLock(fixture, document => document["packages"]!.AsObject().Remove("node_modules/leaf"));
        fixture.Check().Errors.ShouldContain(error => error.Contains("missing", StringComparison.Ordinal));
    }

    [Fact]
    public void R3_UnrelatedLogicalBranchCannotBorrowNestedOverride()
    {
        using var fixture = OverrideGraph("1.1.0", additionalRoot: true);
        Overrides(fixture, new JsonObject { ["ancestor@1.0.0"] = new JsonObject { ["middle@2.0.0"] = new JsonObject { ["leaf"] = "1.1.0" } } });
        fixture.Check().Errors.ShouldContain(error => error.Contains("exact dependency", StringComparison.Ordinal));
    }

    [Fact]
    public void R3_GlobalOverrideAppliesThroughHoistedDescendants()
    {
        using var fixture = OverrideGraph("1.1.0");
        Overrides(fixture, new JsonObject { ["leaf"] = "1.1.0" });
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Fact]
    public void R3_QualifiedReplacementMatchesOriginalRequest_NotResolvedVersion()
    {
        using var fixture = OverrideGraph("1.1.0");
        Overrides(fixture, new JsonObject { ["leaf@1.0.0"] = "1.1.0" });
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Fact]
    public void R3_QualifiedScopeSupportsActualTildeRequest()
    {
        using var fixture = OverrideGraph("1.1.0");
        NpmReviewTests.SetDeclaration(fixture, "node_modules/ancestor", "dependencies", new JsonObject { ["middle"] = "~2.0.0" });
        Overrides(fixture, new JsonObject { ["ancestor@1.0.0"] = new JsonObject { ["middle@2.0.0"] = new JsonObject { ["leaf"] = "1.1.0" } } });
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Fact]
    public void R3_QualifiedScopeValidatesItsOwnEffectiveVersion()
    {
        using var fixture = OverrideGraph("1.0.0");
        NpmReviewTests.SetDeclaration(fixture, "node_modules/ancestor", "dependencies", new JsonObject { ["middle"] = "~2.0.0" });
        var metadataPath = fixture.Full(".github/scripts/node_modules/middle/package.json");
        var metadata = JsonNode.Parse(File.ReadAllText(metadataPath))!;
        metadata["version"] = "2.0.1";
        File.WriteAllText(metadataPath, metadata.ToJsonString());
        EditLock(fixture, document => document["packages"]!["node_modules/middle"]!["version"] = "2.0.1");
        Overrides(fixture, new JsonObject { ["middle@2.0.0"] = new JsonObject { ["leaf"] = "1.0.0" } });
        fixture.Check().Errors.ShouldContain(error => error.Contains("exact dependency middle@2.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public void R3_LogicalCyclesTerminateWithoutLosingScope()
    {
        using var fixture = OverrideGraph("1.1.0");
        NpmReviewTests.SetDeclaration(fixture, "node_modules/@scope/child", "dependencies",
            new JsonObject { ["leaf"] = "1.0.0", ["ancestor"] = "1.0.0" });
        Overrides(fixture, new JsonObject { ["ancestor@1.0.0"] = new JsonObject { ["middle@2.0.0"] = new JsonObject { ["leaf"] = "1.1.0" } } });
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{"ancestor@^1":{"leaf":"1.1.0"}}""")]
    [InlineData("""{"leaf":"^1.1.0"}""")]
    [InlineData("""{"leaf":"$ancestor"}""")]
    [InlineData("""{"leaf":{"." :"1.1.0"}}""")]
    [InlineData("""{"leaf@*":"1.1.0"}""")]
    public void R3_UnsupportedOverrideForms_FailExplicitly(string json)
    {
        using var fixture = OverrideGraph("1.0.0");
        Overrides(fixture, JsonNode.Parse(json)!.AsObject());
        fixture.Check().Errors.ShouldContain(error => error.Contains("Unsupported npm override", StringComparison.Ordinal));
    }

    private static AuditFixture OverrideGraph(string leafVersion, bool additionalRoot = false)
    {
        var fixture = new AuditFixture();
        fixture.Npm("ancestor", "MIT", AuditFixture.Mit);
        fixture.Npm("middle", "MIT", AuditFixture.Mit, version: "2.0.0");
        fixture.Npm("@scope/child", "MIT", AuditFixture.Mit, version: "2.0.0");
        fixture.Npm("leaf", "MIT", AuditFixture.Mit, version: leafVersion);
        var dependencies = new JsonObject { ["ancestor"] = "1.0.0" };
        if (additionalRoot)
        {
            fixture.Npm("outside", "MIT", AuditFixture.Mit);
            fixture.Npm("bridge", "MIT", AuditFixture.Mit);
            dependencies["outside"] = "1.0.0";
            NpmReviewTests.SetDeclaration(fixture, "node_modules/outside", "dependencies", new JsonObject { ["bridge"] = "1.0.0" });
            NpmReviewTests.SetDeclaration(fixture, "node_modules/bridge", "dependencies", new JsonObject { ["middle"] = "2.0.0" });
        }
        NpmReviewTests.SetDeclaration(fixture, "", "dependencies", dependencies);
        NpmReviewTests.SetDeclaration(fixture, "node_modules/ancestor", "dependencies", new JsonObject { ["middle"] = "2.0.0" });
        NpmReviewTests.SetDeclaration(fixture, "node_modules/middle", "dependencies",
            new JsonObject { ["leaf"] = "1.0.0", ["@scope/child"] = "2.0.0" });
        NpmReviewTests.SetDeclaration(fixture, "node_modules/@scope/child", "dependencies", new JsonObject { ["leaf"] = "1.0.0" });
        return fixture;
    }

    private static void Overrides(AuditFixture fixture, JsonObject overrides)
    {
        var path = fixture.Full(".github/scripts/package.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document["overrides"] = overrides;
        File.WriteAllText(path, document.ToJsonString());
    }

    private static void EditLock(AuditFixture fixture, Action<JsonNode> edit)
    {
        var path = fixture.Full(".github/scripts/package-lock.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        edit(document);
        File.WriteAllText(path, document.ToJsonString());
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        var start = new ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, output + error);
    }
}
