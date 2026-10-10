using System.Text.Json;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class DocsToolingPolicyTests
{
    [Theory]
    [InlineData("EPL-2.0")]
    [InlineData("MPL-2.0")]
    [InlineData("MIT")]
    public void VerifiedDocsOnlyToolingIsVisibleButNotCountedAsLicensedAcceptance(string license)
    {
        using var fixture = Setup(license, ReviewRegressionTests.CompleteLicense(license));
        var report = fixture.Check();
        report.Errors.ShouldBeEmpty();
        report.Packages.ShouldNotContain(item => item.Package == "docs-tool");
        var tooling = JsonSerializer.SerializeToElement(report).GetProperty("DocsTooling").EnumerateArray()
            .Single(item => item.GetProperty("Package").GetString() == "docs-tool");
        tooling.GetProperty("TierPolicy").GetString().ShouldBe("exempt-docs-generation");
        tooling.GetProperty("EvidenceErrors").GetArrayLength().ShouldBe(0);
        tooling.GetProperty("Evidence").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("GPL-3.0-only")]
    [InlineData("LGPL-3.0-or-later")]
    [InlineData("AGPL-3.0-only")]
    [InlineData("SSPL-1.0")]
    public void DocsOnlyProhibitedTierIsNonblockingButMissingTextRemainsAnEvidenceFailure(string license)
    {
        using var fixture = Setup(license, null);
        var report = fixture.Check();
        report.Errors.ShouldContain(error => error.Contains("license text unavailable", StringComparison.Ordinal));
        report.Errors.ShouldNotContain(error => error.Contains("prohibited", StringComparison.Ordinal));
        report.Packages.ShouldNotContain(item => item.Package == "docs-tool");
        var record = JsonSerializer.SerializeToElement(report).GetProperty("DocsTooling").EnumerateArray()
            .Single(item => item.GetProperty("Package").GetString() == "docs-tool");
        record.GetProperty("TierPolicy").GetString().ShouldBe("exempt-docs-generation");
        record.GetProperty("DeclaredLicense").GetString().ShouldBe(license);
        record.GetProperty("EvidenceErrors").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public void UnknownSupplementalTermsStillFailForExemptTooling()
    {
        using var fixture = Setup("MIT", AuditFixture.Mit);
        fixture.WriteText("docs/site/node_modules/docs-tool/NOTICE.txt", "Unrecognized additional requirements.");
        fixture.Check().Errors.ShouldContain(error => error.Contains("unrecognized supplemental license text", StringComparison.Ordinal));
    }

    [Fact]
    public void DocsOnlyOrChoiceStillRequiresAnExplicitCompleteSelection()
    {
        using var fixture = Setup("MIT OR EPL-2.0", AuditFixture.Mit + "\n" + AuditFixture.Epl);
        fixture.Check().Errors.ShouldContain(error => error.Contains("recorded explicit selection", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("non-docs")]
    [InlineData("docs-emitted")]
    public void NonDocsOrEmittedIdentityCannotInheritExemption(string kind)
    {
        using var fixture = Setup("EPL-2.0", AuditFixture.Epl);
        if (kind == "non-docs") fixture.Npm("docs-tool", "EPL-2.0", AuditFixture.Epl);
        else DocsFixtureBuilder.Seal(fixture, new DocsPackage("docs-tool", "1.0.0"));
        var report = fixture.Check();
        report.Errors.ShouldNotBeEmpty();
        report.Packages.ShouldNotContain(item => item.Package == "docs-tool");
        JsonSerializer.SerializeToElement(report).GetProperty("DocsTooling").EnumerateArray()
            .ShouldNotContain(item => item.GetProperty("Package").GetString() == "docs-tool");
    }

    [Theory]
    [InlineData("api")]
    [InlineData("cli")]
    [InlineData("docs")]
    [InlineData("collection")]
    public void AnyIncompleteProducerOrCollectionPreventsDocsExemption(string failure)
    {
        using var fixture = Setup("EPL-2.0", AuditFixture.Epl);
        switch (failure)
        {
            case "api": File.Delete(fixture.Full("artifacts/api/Nachos.Api.deps.json")); break;
            case "cli": File.Delete(fixture.Full("artifacts/cli/Nachos.Cli.deps.json")); break;
            case "docs": File.Delete(fixture.Full("docs/site/dist/.nachos/output-provenance.v1.json")); break;
            case "collection": File.Delete(fixture.Full(".github/scripts/node_modules/baseline/package.json")); break;
        }
        var report = fixture.Check();
        report.Errors.ShouldNotBeEmpty();
        var json = JsonSerializer.SerializeToElement(report);
        json.GetProperty("DocsTooling").GetArrayLength().ShouldBe(0);
        json.GetProperty("InputErrors").GetArrayLength().ShouldBeGreaterThan(0);
        report.Errors.ShouldContain(error => error.Contains("EPL-2.0 requires", StringComparison.Ordinal));
    }

    [Fact]
    public void EmittedPermissiveIdentityRemainsDistributedAndSeparateFromTooling()
    {
        using var fixture = Setup("MIT", AuditFixture.Mit);
        DocsFixtureBuilder.Seal(fixture, new DocsPackage("docs-tool", "1.0.0"));
        var report = fixture.Check();
        report.Errors.ShouldBeEmpty();
        report.Packages.Single(item => item.Package == "docs-tool").Tier.ShouldBe("distributed");
    }

    [Fact]
    public void ProhibitedDocsTierMayUseExistingExplicitEvidenceReviewWithoutShippingApproval()
    {
        using var fixture = Setup("LGPL-3.0-or-later", null);
        fixture.Override("npm", "docs-tool", "1.0.0", "LGPL-3.0-or-later");
        var report = fixture.Check();
        report.Errors.ShouldBeEmpty();
        report.DocsTooling.Single(item => item.Package == "docs-tool").SelectedLicense.ShouldBe("LGPL-3.0-or-later");
        report.Packages.ShouldNotContain(item => item.Package == "docs-tool");
    }

    [Fact]
    public void CrossOriginMissingMetadataCannotEraseTheNonDocsIdentity()
    {
        using var fixture = Setup("EPL-2.0", AuditFixture.Epl);
        fixture.Npm("docs-tool", "EPL-2.0", AuditFixture.Epl);
        File.Delete(fixture.Full(".github/scripts/node_modules/docs-tool/package.json"));
        var report = fixture.Check();
        report.InputErrors.ShouldContain(error => error.Stage == "collection");
        report.DocsTooling.ShouldBeEmpty();
        report.Errors.ShouldContain(error => error.Contains("EPL-2.0 requires", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DevFlagDoesNotGrantOrRemoveDocsOnlyScope(bool dev)
    {
        using var fixture = Setup("EPL-2.0", AuditFixture.Epl);
        fixture.Npm("another-tool", "EPL-2.0", AuditFixture.Epl, dev: dev, location: "docs/site");
        DocsFixtureBuilder.Seal(fixture);
        var report = fixture.Check();
        report.Errors.ShouldBeEmpty();
        report.DocsTooling.ShouldContain(item => item.Package == "another-tool");
    }

    [Fact]
    public void OtherArtifactUnattributedContentPreventsDocsOnlyPermission()
    {
        using var fixture = Setup("EPL-2.0", AuditFixture.Epl);
        fixture.WriteText("artifacts/api/copy-from-docs.js", "Unattributed copied asset.");
        var report = fixture.Check();
        report.InputErrors.ShouldContain(error => error.Stage == "provenance");
        report.DocsTooling.ShouldBeEmpty();
    }

    [Fact]
    public void DocsGenerationCannotDropAnAndObligationThroughOverride()
    {
        using var fixture = Setup("MIT AND EPL-2.0", AuditFixture.Mit);
        fixture.Override("npm", "docs-tool", "1.0.0", "MIT");
        fixture.Check().Errors.ShouldContain(error => error.Contains("AND obligation", StringComparison.Ordinal));
    }

    private static AuditFixture Setup(string license, string? text)
    {
        var fixture = new AuditFixture();
        fixture.Docs();
        fixture.Npm("docs-tool", license, text, location: "docs/site");
        DocsFixtureBuilder.Seal(fixture);
        return fixture;
    }
}
