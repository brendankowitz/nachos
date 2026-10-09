using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

// Retained gap probes now exercise the reviewed complete-output contract.
public sealed class DocsToolingProvenanceGapTests
{
    private const string Libvips = "@img/sharp-libvips-linux-x64";
    private const string Version = "1.3.4";
    private const string License = "LGPL-3.0-or-later";
    private const string Manifest = "docs/site/dist/.nachos/bundle-modules.json";

    [Fact]
    public void Gap_CapturedLibvipsAbsentFromFlatListNeedsExplicitProducerProvenanceFailure()
    {
        using var fixture = DocsFixture();
        AddCapturedLibvips(fixture, "docs/site");
        File.Delete(fixture.Full("docs/site/dist/.nachos/output-provenance.v1.json"));
        fixture.WriteText("docs/site/dist/index.html", "<html><body>First-party fixture page.</body></html>");
        var report = fixture.Check();
        report.Errors.ShouldContain(error => error.Contains("provenance", StringComparison.OrdinalIgnoreCase));
        report.Packages.ShouldNotContain(package => package.Package == Libvips);
    }

    [Theory]
    [InlineData("client.js")]
    [InlineData("styles.css")]
    [InlineData("font.woff2")]
    [InlineData("icon.svg")]
    [InlineData("embedded-content.png")]
    public void Gap_EmittedThirdPartyContentOmittedFromManifestMustFail(string file)
    {
        using var fixture = DocsFixture();
        fixture.Npm("copied-content", "MIT", AuditFixture.Mit, location: "docs/site");
        const string content = "Exact controlled third-party fixture content; not executed.";
        fixture.WriteText("docs/site/node_modules/copied-content/" + file, content);
        fixture.WriteText("docs/site/dist/assets/" + file, content);
        fixture.Check().Errors.ShouldContain(error => error.Contains("Docs provenance", StringComparison.Ordinal));
    }

    [Fact]
    public void Gap_StaleManifestDoesNotProveCurrentEmittedBytes()
    {
        using var fixture = DocsFixture();
        fixture.Npm("client", "MIT", AuditFixture.Mit, location: "docs/site");
        fixture.Write(Manifest, new[] { new { package = "client", version = "1.0.0" } });
        fixture.WriteText("docs/site/dist/client.js", "bytes from an earlier completed build");
        fixture.WriteText("docs/site/dist/client.js", "unattributed replacement after manifest capture");
        fixture.Check().Errors.ShouldContain(error => error.Contains("Docs provenance", StringComparison.Ordinal));
    }

    [Fact]
    public void Gap_ManifestWithoutAnyEmittedSiteCannotEstablishCompletedProducerScope()
    {
        using var fixture = DocsFixture();
        File.Delete(fixture.Full("docs/site/dist/index.html"));
        fixture.Check().Errors.ShouldContain(error => error.Contains("closure", StringComparison.Ordinal));
    }

    [Fact]
    public void Control_RealLibvipsStillAppearsInTheInventoryDiagnosticsBeforePolicyImplementation()
    {
        using var fixture = DocsFixture();
        AddCapturedLibvips(fixture, "docs/site");
        var report = fixture.Check();
        report.Errors.ShouldContain(error => error.Contains($"npm:{Libvips}@{Version} (docs/site/package-lock.json)", StringComparison.Ordinal)
            && error.Contains("license text unavailable", StringComparison.Ordinal));
        report.DocsTooling.ShouldContain(item => item.Package == Libvips && item.DeclaredLicense == License
            && item.TierPolicy == "exempt-docs-generation" && item.EvidenceErrors.Count == 1);
        report.Packages.ShouldNotContain(package => package.Package == Libvips);
    }

    [Fact]
    public void Control_ActualEmittedLibvipsRemainsBlockedEvenWithDevFlagAndSpoofedScope()
    {
        using var fixture = DocsFixture();
        AddCapturedLibvips(fixture, "docs/site");
        fixture.Write(Manifest, new[]
        {
            new { package = Libvips, version = Version, scope = "docs-generation-only", distributed = false, complete = true }
        });
        var report = fixture.Check();
        report.Errors.ShouldContain(error => error.Contains(Libvips, StringComparison.Ordinal)
            && error.Contains("prohibited", StringComparison.Ordinal));
        report.Packages.ShouldNotContain(package => package.Package == Libvips);
    }

    [Fact]
    public void Control_SameIdentityInDocsAndScriptsDoesNotInheritADocsOnlyExemption()
    {
        using var fixture = DocsFixture();
        AddCapturedLibvips(fixture, "docs/site");
        AddCapturedLibvips(fixture, ".github/scripts");
        var report = fixture.Check();
        report.Errors.Count(error => error.Contains(Libvips, StringComparison.Ordinal)
            && error.Contains("prohibited", StringComparison.Ordinal)).ShouldBe(2);
        report.Errors.ShouldContain(error => error.Contains(".github/scripts/package-lock.json", StringComparison.Ordinal)
            && error.Contains(Libvips, StringComparison.Ordinal));
    }

    [Fact]
    public void Control_IdentityEmittedInDocsAlsoGatesItsOtherOrigin()
    {
        using var fixture = DocsFixture();
        AddCapturedLibvips(fixture, "docs/site");
        AddCapturedLibvips(fixture, ".github/scripts");
        DocsFixtureBuilder.Seal(fixture, new DocsPackage(Libvips, Version));
        var report = fixture.Check();
        report.Errors.Count(error => error.Contains(Libvips, StringComparison.Ordinal)
            && error.Contains("prohibited", StringComparison.Ordinal)).ShouldBe(2);
        report.Packages.ShouldNotContain(package => package.Package == Libvips);
    }

    [Fact]
    public void Control_ArbitraryBuildRootNamedDocsGenerationKeepsExistingPolicy()
    {
        using var fixture = DocsFixture();
        AddCapturedLibvips(fixture, "tools/docs-generation");
        fixture.Write("tools/docs-generation/package.json", new
        {
            name = "docs-generation-only", version = "1.0.0", privatePackage = true,
            scope = "docs-generation", distributed = false
        });
        var locked = JsonNode.Parse(File.ReadAllText(fixture.Full("tools/docs-generation/package-lock.json")))!;
        locked["packages"]![""]!["name"] = "docs-generation-only";
        fixture.Write("tools/docs-generation/package-lock.json", locked);
        var report = fixture.Check();
        report.Errors.ShouldContain(error => error.Contains(Libvips, StringComparison.Ordinal)
            && error.Contains("prohibited", StringComparison.Ordinal));
        report.Packages.ShouldNotContain(package => package.Package == Libvips);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("unlocked")]
    public void Control_MissingInvalidOrUnboundManifestIsNeverProofOfNonDistribution(string change)
    {
        using var fixture = DocsFixture();
        switch (change)
        {
            case "missing":
                File.Delete(fixture.Full(Manifest));
                break;
            case "malformed":
                fixture.WriteText(Manifest, "{\"complete\":true,\"scope\":\"docs-generation\"}");
                break;
            case "duplicate":
                fixture.Write(Manifest, new[]
                {
                    new { package = "site-baseline", version = "1.0.0" },
                    new { package = "site-baseline", version = "1.0.0" }
                });
                break;
            case "unlocked":
                fixture.Write(Manifest, new[] { new { package = "unlocked-emitter", version = "1.0.0" } });
                break;
        }
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Control_MissingInstalledMetadataRemainsAnEvidenceFailure()
    {
        using var fixture = DocsFixture();
        AddCapturedLibvips(fixture, "docs/site");
        File.Delete(fixture.Full("docs/site/node_modules/" + Libvips + "/package.json"));
        var report = fixture.Check();
        report.Errors.ShouldNotBeEmpty();
        report.Packages.ShouldNotContain(package => package.Package == Libvips);
    }

    [Fact]
    public void Control_DevFalseDoesNotHideTheRealDocsLockedIdentity()
    {
        using var fixture = DocsFixture();
        AddCapturedLibvips(fixture, "docs/site");
        var path = "docs/site/package-lock.json";
        var locked = JsonNode.Parse(File.ReadAllText(fixture.Full(path)))!;
        locked["packages"]!["node_modules/" + Libvips]!["dev"] = false;
        fixture.Write(path, locked);
        DocsFixtureBuilder.Seal(fixture);
        fixture.Check().DocsTooling.ShouldContain(item => item.Package == Libvips && item.DeclaredLicense == License);
    }

    private static AuditFixture DocsFixture()
    {
        var fixture = new AuditFixture();
        fixture.Docs();
        DocsFixtureBuilder.Seal(fixture);
        return fixture;
    }

    private static void AddCapturedLibvips(AuditFixture fixture, string location)
    {
        fixture.Npm(Libvips, License, null, Version, dev: true, location: location);
        var documents = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "docs-libvips-evidence.json")))!;
        foreach (var (name, expected) in new[]
        {
            ("package.json", "2C5F6F9ABF74C1492622D5DC4E4DA056AB0712FD71DE61E03A687C88B8CDAB4E"),
            ("README.md", "B4FB1A9F5909CFD58EDB4177C2028CE3033CF8651AEF740A554C1DC58B3C5EE3")
        })
        {
            var bytes = Convert.FromBase64String(documents[name]!.GetValue<string>());
            Convert.ToHexString(SHA256.HashData(bytes)).ShouldBe(expected);
            File.WriteAllBytes(fixture.Full(location + "/node_modules/" + Libvips + "/" + name), bytes);
        }
        if (location == "docs/site") DocsFixtureBuilder.Seal(fixture);
    }
}
