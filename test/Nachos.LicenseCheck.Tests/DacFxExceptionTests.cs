using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class DacFxExceptionTests
{
    private const string Package = "Microsoft.SqlServer.DacFx";
    private const string Version = "170.4.83";
    private const string License = "LicenseRef-Microsoft-SQL-Server-DacFx-170.4.83";
    private const string Approval = "https://github.com/brendankowitz/nachos/issues/2#issuecomment-6048060868";
    private const string LicensePath = "eng/licenses/Microsoft.SqlServer.DacFx/170.4.83/license.txt";
    private const string LicenseHash = "f6b3be3e53b8b6836b9c08fee9e9fb24e698df2ab2a7e4afd1f77ebf10c22a83";
    private const string Notices = "THIRD-PARTY-NOTICES.md";

    [Theory]
    [InlineData("api")]
    [InlineData("cli")]
    [InlineData("both")]
    public void ExactApprovedPrimaryAndNoticeCopies_PassInAuthorizedArtifactScope(string scope)
    {
        using var fixture = Fixture(scope: scope);
        var report = fixture.Check();
        report.Errors.ShouldBeEmpty();
        var decision = report.Packages.Single(package => package.Package == Package);
        decision.Tier.ShouldBe("distributed");
        decision.SelectedLicense.ShouldBe(License);
        decision.Evidence.ShouldContain(Approval);
        decision.Evidence.ShouldContain("sha256:" + LicenseHash);
    }

    [Fact]
    public void CheckedInExceptionRecord_QualifiesWithoutInventedFixtureApproval()
    {
        using var fixture = Fixture();
        File.Copy(Evidence("license-exceptions.json"), fixture.Full("eng/license-exceptions.json"), overwrite: true);
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ownerApproval", null)]
    [InlineData("ownerApproval", "https://example.test/approval")]
    [InlineData("review", "https://example.test/review")]
    [InlineData("tier", null)]
    [InlineData("tier", "tooling")]
    [InlineData("tier", "distributed")]
    [InlineData("licenseEvidence", "eng/other-license.txt")]
    [InlineData("licenseSha256", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("evidenceUrl", "https://example.test/license")]
    [InlineData("package", "Microsoft.Other.Proprietary")]
    [InlineData("version", "170.5.96")]
    [InlineData("license", "MIT")]
    [InlineData("selectedLicense", "MIT")]
    public void MissingOrWrongAuthorizationField_Fails(string field, string? value)
    {
        using var fixture = Fixture();
        var approval = Record();
        approval[field] = value;
        fixture.Write("eng/license-exceptions.json", new[] { approval });
        Reject(fixture);
    }

    [Theory]
    [InlineData("docs")]
    [InlineData("api")]
    public void ChangedApprovalArtifactScope_Fails(string scope)
    {
        using var fixture = Fixture();
        var approval = Record();
        approval["artifactScopes"] = new JsonArray(scope);
        fixture.Write("eng/license-exceptions.json", new[] { approval });
        Reject(fixture);
    }

    [Fact]
    public void OwnerRecordIsRequired_NotJustTheRealLicenseBytes()
    {
        using var fixture = Fixture();
        fixture.Write("eng/license-exceptions.json", Array.Empty<object>());
        Reject(fixture);
    }

    [Fact]
    public void ShippedApprovalDoesNotBecomeAToolingPermission()
    {
        using var fixture = Fixture(scope: "tooling");
        Reject(fixture);
    }

    [Fact]
    public void VersionDrift_RequiresNewReview()
    {
        using var fixture = Fixture(version: "170.5.96");
        Reject(fixture);
    }

    [Fact]
    public void OtherProprietaryPackageWithIdenticalText_IsNotAuthorized()
    {
        using var fixture = Fixture(package: "Microsoft.Other.Proprietary");
        Reject(fixture);
    }

    [Theory]
    [InlineData("altered")]
    [InlineData("truncated")]
    [InlineData("missing")]
    public void PrimaryTextMustMatchActualReviewedBytes(string change)
    {
        var text = File.ReadAllText(Evidence("license.txt"));
        using var fixture = Fixture(primary: change switch
        {
            "altered" => text + "\nAdditional redistribution restriction.",
            "truncated" => text[..(text.Length / 2)],
            _ => null
        }, useOriginalPrimary: false);
        Reject(fixture);
    }

    [Fact]
    public void ChangedRawBytesWithIdenticalDecodedText_FailFingerprint()
    {
        using var fixture = Fixture();
        ReplaceEntry(fixture, "license.txt", Encoding.UTF8.GetPreamble().Concat(File.ReadAllBytes(Evidence("license.txt"))).ToArray());
        Reject(fixture);
    }

    [Theory]
    [InlineData("MIT")]
    [InlineData(License + " AND Apache-2.0")]
    [InlineData(License + " OR MIT")]
    [InlineData("GPL-3.0-only")]
    public void ApprovalDoesNotDiscardMetadataObligationsOrChoices(string expression)
    {
        using var fixture = Fixture();
        ReplaceEntry(fixture, Package + ".nuspec", Encoding.UTF8.GetBytes(
            $"<package><metadata><id>{Package}</id><version>{Version}</version><license type=\"expression\">{expression}</license></metadata></package>"));
        Reject(fixture);
    }

    [Fact]
    public void ExtraUnknownPrimaryDocument_CannotDisappear()
    {
        using var fixture = Fixture();
        ReplaceEntry(fixture, Package + ".nuspec", Encoding.UTF8.GetBytes(
            $"<package><metadata><id>{Package}</id><version>{Version}</version></metadata></package>"));
        fixture.NugetEntry(Package, "LICENSE-ADDITIONAL.txt", "Unrecognized additional primary restrictions.", Version);
        Reject(fixture);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("prohibited")]
    public void SupplementalTermsAreNotCoveredByThePrimaryException(string kind)
    {
        using var fixture = Fixture();
        fixture.NugetEntry(Package, "THIRD-PARTY-NOTICES.txt",
            kind == "unknown" ? "Unknown supplemental restrictions." : AuditFixture.Gpl, Version);
        Reject(fixture);
    }

    [Fact]
    public void KnownSupplementalObligation_IsRetainedConjunctively()
    {
        using var fixture = Fixture();
        fixture.NugetEntry(Package, "THIRD-PARTY-NOTICES.txt", AuditFixture.Mit, Version);
        var report = fixture.Check();
        report.Errors.ShouldBeEmpty();
        report.Packages.Single(package => package.Package == Package).SelectedLicense.ShouldBe(License + " AND MIT");
    }

    [Fact]
    public void GenericEvidenceOverrideCannotRelabelApprovedPrimary()
    {
        using var fixture = Fixture();
        fixture.Override("nuget", Package, Version, "MIT");
        Reject(fixture);
    }

    [Theory]
    [InlineData(Version, false)]
    [InlineData("170.5.96", false)]
    [InlineData("170.5.96", true)]
    public void GenericOverrideCannotReplaceMissingOrDifferentVersionAuthorization(string version, bool keepApproval)
    {
        using var fixture = Fixture(version: version);
        if (!keepApproval)
        {
            fixture.Write("eng/license-exceptions.json", Array.Empty<object>());
        }
        fixture.Override("nuget", Package, version, "MIT");
        var report = fixture.Check();
        report.Errors.ShouldContain(error => error.Contains("override cannot discard an observed license component", StringComparison.Ordinal));
        report.Packages.ShouldNotContain(package => package.Ecosystem == "nuget");
    }

    [Theory]
    [InlineData(Notices, false)]
    [InlineData(Notices, true)]
    [InlineData(LicensePath, false)]
    [InlineData(LicensePath, true)]
    public void MissingOrTamperedPublishedNotice_Fails(string relative, bool tamper)
    {
        using var fixture = Fixture();
        var path = fixture.Full("artifacts/api/" + relative);
        if (tamper)
        {
            File.AppendAllText(path, "\nTampered evidence.");
        }
        else
        {
            File.Delete(path);
        }
        Reject(fixture);
    }

    [Theory]
    [InlineData(Notices)]
    [InlineData(LicensePath)]
    public void ChangingBothSourceAndCopiedNotice_DoesNotAuthorizeNewText(string relative)
    {
        using var fixture = Fixture();
        fixture.WriteText(relative, "Changed source evidence.");
        fixture.WriteText("artifacts/api/" + relative, "Changed source evidence.");
        Reject(fixture);
    }

    [Fact]
    public void SecondShippingArtifactAlsoRequiresNotices()
    {
        using var fixture = Fixture(scope: "both");
        File.Delete(fixture.Full("artifacts/cli/" + Notices));
        Reject(fixture);
    }

    [Fact]
    public void CopiedSourceNoticesDoNotProveDacFxBinariesShipped()
    {
        using var fixture = Fixture(scope: "tooling");
        CopyNotices(fixture, "api");
        Reject(fixture);
    }

    [Fact]
    public void SkeletonMayCopyProvedSourceNotices_WithoutPretendingDacFxIsInstalled()
    {
        using var fixture = new AuditFixture();
        CopySources(fixture);
        CopyNotices(fixture, "api");
        var report = fixture.Check();
        report.Errors.ShouldBeEmpty();
        report.Packages.ShouldNotContain(package => package.Package == Package);
    }

    [Fact]
    public void ArbitraryMarkdownIsNotFirstPartyNoticeEvidence()
    {
        using var fixture = new AuditFixture();
        fixture.WriteText("arbitrary.md", "Unproven content.");
        fixture.WriteText("artifacts/api/arbitrary.md", "Unproven content.");
        fixture.Check().Errors.ShouldContain(error => error.Contains("Unproven copied artifact", StringComparison.Ordinal));
    }

    private static void Reject(AuditFixture fixture)
    {
        var report = fixture.Check();
        report.Errors.ShouldNotBeEmpty();
        report.Packages.ShouldNotContain(package => package.Ecosystem == "nuget");
    }

    private static AuditFixture Fixture(string scope = "api", string package = Package, string version = Version,
        string? primary = null, bool useOriginalPrimary = true)
    {
        var fixture = new AuditFixture();
        CopySources(fixture);
        fixture.Write("eng/license-exceptions.json", new[] { Record() });
        fixture.Nuget(package, "license.txt", useOriginalPrimary ? File.ReadAllText(Evidence("license.txt")) : primary,
            licenseType: "file", version: version);
        if (scope is "api" or "both")
        {
            fixture.Publish(package, version);
            CopyNotices(fixture, "api");
        }
        if (scope is "cli" or "both")
        {
            fixture.Write("artifacts/cli/Nachos.Cli.deps.json", new
            {
                libraries = new Dictionary<string, object>
                {
                    ["Nachos.Cli/1.0.0"] = new { type = "project" },
                    [package + "/" + version] = new { type = "package" }
                }
            });
            CopyNotices(fixture, "cli");
        }
        return fixture;
    }

    private static JsonObject Record() => new()
    {
        ["ecosystem"] = "nuget", ["package"] = Package, ["version"] = Version, ["license"] = License,
        ["tier"] = "shipped", ["artifactScopes"] = new JsonArray("api", "cli"),
        ["reviewers"] = new JsonArray("repository-owner"), ["review"] = Approval, ["ownerApproval"] = Approval,
        ["licenseEvidence"] = LicensePath, ["licenseSha256"] = LicenseHash,
        ["evidenceUrl"] = "https://www.nuget.org/packages/Microsoft.SqlServer.DacFx/170.4.83/License",
        ["purpose"] = "Schema deployment in the API and CLI.",
        ["restriction"] = "Exact approved version only; redistribution conditions remain release-owner obligations."
    };

    private static string Evidence(string file) => Path.Combine(AppContext.BaseDirectory, "DacFxEvidence", file);

    private static void ReplaceEntry(AuditFixture fixture, string entryName, byte[] bytes)
    {
        using var archive = ZipFile.Open(fixture.Full($"cache/microsoft.sqlserver.dacfx/{Version}/microsoft.sqlserver.dacfx.{Version}.nupkg"), ZipArchiveMode.Update);
        archive.GetEntry(entryName)!.Delete();
        using var stream = archive.CreateEntry(entryName).Open();
        stream.Write(bytes);
    }

    private static void CopySources(AuditFixture fixture)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Full(LicensePath))!);
        File.Copy(Evidence("license.txt"), fixture.Full(LicensePath));
        File.Copy(Evidence(Notices), fixture.Full(Notices));
    }

    private static void CopyNotices(AuditFixture fixture, string scope)
    {
        foreach (var relative in new[] { Notices, LicensePath })
        {
            var target = fixture.Full("artifacts/" + scope + "/" + relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(fixture.Full(relative), target);
        }
    }
}
