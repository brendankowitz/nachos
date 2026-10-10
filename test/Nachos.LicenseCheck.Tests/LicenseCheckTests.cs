using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Nachos.LicenseCheck;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class LicenseCheckTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task CommandLine_WritesReportAndReturnsPolicyExitCode(bool prohibited, int exitCode)
    {
        using var fixture = new AuditFixture();
        if (prohibited)
        {
            fixture.Npm("bad", "GPL-3.0-only", AuditFixture.Gpl);
        }
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var argument in new[]
        {
            typeof(LicenseAudit).Assembly.Location, "--repo", fixture.Root, "--nuget-inventory", fixture.Full("nuget.json"),
            "--nuget-cache", fixture.Full("cache"), "--api-publish", fixture.Full("artifacts/api"),
            "--cli-publish", fixture.Full("artifacts/cli"), "--report", fixture.Full("report.json")
        })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        process.ExitCode.ShouldBe(exitCode, await stderr + await stdout);
        File.Exists(fixture.Full("report.json")).ShouldBeTrue();
        using var report = JsonDocument.Parse(File.ReadAllText(fixture.Full("report.json")));
        report.RootElement.GetProperty("errors").GetArrayLength().ShouldBe(prohibited ? 1 : 0);
    }

    [Fact]
    public void GplPackage_Fails()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("bad", "GPL-3.0-only", AuditFixture.Gpl);
        fixture.Check().Errors.ShouldContain(error => error.Contains("prohibited", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownLicense_FailsClosed()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("unknown", "LicenseRef-Private", "All rights reserved.");
        fixture.Check().Errors.ShouldContain(error => error.Contains("unrecognized", StringComparison.Ordinal));
    }

    [Fact]
    public void EplInTooling_WithException_Passes()
    {
        using var fixture = new AuditFixture();
        fixture.Elk();
        fixture.Check().Errors.ShouldBeEmpty();
        fixture.Check().Packages.ShouldContain(package => package.Package == "elkjs" && package.Tier == "tooling");
    }

    [Fact]
    public void EplInShippedProject_Fails()
    {
        using var fixture = new AuditFixture();
        fixture.Nuget("epl", "EPL-2.0", AuditFixture.Epl);
        fixture.Publish("epl", "1.0.0");
        fixture.Check().Errors.ShouldContain(error => error.Contains("distributed", StringComparison.Ordinal));
    }

    [Fact]
    public void OrExpression_RecordsSelectedLicense()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("dual", "MIT OR Apache-2.0", AuditFixture.Mit + "\n" + AuditFixture.Apache);
        fixture.Override("npm", "dual", "1.0.0", "MIT OR Apache-2.0", "MIT");
        var report = fixture.Check();
        report.Errors.ShouldBeEmpty();
        report.Packages.Single(package => package.Package == "dual").SelectedLicense.ShouldBe("MIT");
    }

    [Fact]
    public void OrExpression_WithoutRecordedChoice_Fails()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("dual", "MIT OR Apache-2.0", AuditFixture.Mit + "\n" + AuditFixture.Apache);
        fixture.Check().Errors.ShouldContain(error => error.Contains("selection", StringComparison.Ordinal));
    }

    [Fact]
    public void DisallowedPythonDependency_Fails()
    {
        using var fixture = new AuditFixture();
        fixture.Python("client", "AGPL-3.0-only", "GNU Affero General Public License\nVersion 3, 19 November 2007");
        fixture.Check().Errors.ShouldContain(error => error.Contains("prohibited", StringComparison.Ordinal));
    }

    [Fact]
    public void MetadataTextDisagreement_Fails()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("mismatch", "Apache-2.0", AuditFixture.Mit);
        fixture.Check().Errors.ShouldContain(error => error.Contains("disagree", StringComparison.Ordinal));
    }

    [Fact]
    public void LicenseTextUnavailable_FailsWithoutOverride()
    {
        using var fixture = new AuditFixture();
        fixture.Nuget("url-only", "MIT", null);
        fixture.Check().Errors.ShouldContain(error => error.Contains("text unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void ExceptedToolingPackageInDepsJson_Fails()
    {
        using var fixture = new AuditFixture();
        fixture.Nuget("epl", "EPL-2.0", AuditFixture.Epl);
        fixture.Exception("nuget", "epl", "1.0.0", "EPL-2.0");
        fixture.Publish("epl", "1.0.0");
        fixture.Check().Errors.ShouldContain(error => error.Contains("excepted package", StringComparison.Ordinal));
    }

    [Fact]
    public void ExceptedToolingPackageInDocsBundle_Fails()
    {
        using var fixture = new AuditFixture();
        fixture.Elk();
        fixture.Docs(new { package = "elkjs", version = "0.9.3" });
        fixture.Npm("elkjs", "EPL-2.0", AuditFixture.Epl, "0.9.3", location: "docs/site");
        DocsFixtureBuilder.Seal(fixture, new DocsPackage("elkjs", "0.9.3"));
        fixture.Check().Errors.ShouldContain(error => error.Contains("excepted package", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("MIT OR")]
    [InlineData("MIT AND (ISC")]
    [InlineData("MIT garbage")]
    [InlineData("()")]
    [InlineData("MIT WITH Custom-exception")]
    [InlineData("MIT or ISC")]
    public void MalformedOrUnsupportedSpdx_Fails(string expression)
    {
        using var fixture = new AuditFixture();
        fixture.Npm("broken", expression, AuditFixture.Mit);
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("MIT AND Apache-2.0", true)]
    [InlineData("MIT AND EPL-2.0", false)]
    [InlineData("MIT OR GPL-3.0-only", false)]
    public void EveryComponent_IsEvaluated(string expression, bool passes)
    {
        using var fixture = new AuditFixture();
        var other = expression.Contains("Apache", StringComparison.Ordinal) ? AuditFixture.Apache
            : expression.Contains("EPL", StringComparison.Ordinal) ? AuditFixture.Epl : AuditFixture.Gpl;
        fixture.Npm("combined", expression, AuditFixture.Mit + "\n" + other);
        fixture.Check().Errors.Count.ShouldBe(passes ? 0 : 1);
    }

    [Fact]
    public void NestedOr_RequiresACompleteValidBranch()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("dual", "(MIT OR Apache-2.0) AND ISC", AuditFixture.Mit + AuditFixture.Apache + AuditFixture.Isc);
        fixture.Override("npm", "dual", "1.0.0", "(MIT OR Apache-2.0) AND ISC", "MIT");
        fixture.Check().Errors.ShouldNotBeEmpty();
        fixture.Override("npm", "dual", "1.0.0", "(MIT OR Apache-2.0) AND ISC", "MIT AND ISC");
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("GPL-3.0-only", "GNU GENERAL PUBLIC LICENSE Version 3")]
    [InlineData("LGPL-2.1-only", "GNU LESSER GENERAL PUBLIC LICENSE Version 2.1")]
    [InlineData("SSPL-1.0", "Server Side Public License Version 1")]
    [InlineData("EPL-2.0", "complete-epl")]
    public void Override_CannotLaunderRestrictiveText(string actual, string text)
    {
        text = actual == "EPL-2.0" ? AuditFixture.Epl : text;
        using var fixture = new AuditFixture();
        fixture.Npm("laundered", actual, text);
        fixture.Override("npm", "laundered", "1.0.0", "MIT");
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void PermissiveMetadata_CannotHideProhibitedText()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("laundered", "MIT", AuditFixture.Mit + AuditFixture.Gpl);
        fixture.Override("npm", "laundered", "1.0.0", "MIT");
        fixture.Check().Errors.ShouldContain(error => error.Contains("prohibited", StringComparison.Ordinal));
    }

    [Fact]
    public void ExactReviewedOverride_CanSupplyMissingEvidence()
    {
        using var fixture = new AuditFixture();
        fixture.Nuget("legacy", "MIT", null);
        fixture.Override("nuget", "legacy", "1.0.0", "MIT");
        var report = fixture.Check();
        report.Errors.ShouldBeEmpty();
        report.Packages.Single(package => package.Package == "legacy").Evidence.ShouldContain("https://example.test/evidence");
    }

    [Fact]
    public void DifferentVersionOverride_DoesNotApply()
    {
        using var fixture = new AuditFixture();
        fixture.Nuget("legacy", "MIT", null);
        fixture.Override("nuget", "legacy", "2.0.0", "MIT");
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void DevFlag_DoesNotControlDistribution()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("permissive", "MIT", AuditFixture.Mit, dev: true, location: "docs/site");
        fixture.Docs(new { package = "permissive", version = "1.0.0" });
        DocsFixtureBuilder.Seal(fixture, new DocsPackage("permissive", "1.0.0"));
        fixture.Check().Packages.Single(package => package.Package == "permissive").Tier.ShouldBe("distributed");
    }

    [Fact]
    public void MissingRequiredProvenance_Fails()
    {
        using var fixture = new AuditFixture();
        File.Delete(Path.Combine(fixture.Root, "artifacts", "api", "Nachos.Api.deps.json"));
        fixture.Check().Errors.ShouldContain(error => error.Contains("deps.json", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingDocsManifest_FailsWhenSiteExists()
    {
        using var fixture = new AuditFixture();
        fixture.Docs();
        File.Delete(Path.Combine(fixture.Root, "docs", "site", "dist", ".nachos", "bundle-modules.json"));
        fixture.Check().Errors.ShouldContain(error => error.Contains("bundle-modules.json", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingInventory_FailsRatherThanPassingEmpty()
    {
        using var fixture = new AuditFixture();
        File.Delete(Path.Combine(fixture.Root, "nuget.json"));
        fixture.Check().Errors.ShouldContain(error => error.Contains("nuget.json", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingNpmInstallation_Fails()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("missing", "MIT", AuditFixture.Mit);
        Directory.Delete(Path.Combine(fixture.Root, ".github", "scripts", "node_modules", "missing"), true);
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void NpmInstallationVersionMustMatchLock()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("wrong", "MIT", AuditFixture.Mit);
        fixture.Write(".github/scripts/node_modules/wrong/package.json", new { name = "wrong", version = "2.0.0", license = "MIT" });
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void BundlePackageMustExistInInventory()
    {
        using var fixture = new AuditFixture();
        fixture.Docs(new { package = "unknown", version = "1.0.0" });
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void ExceptionDoesNotTravelToAnotherNpmRoot()
    {
        using var fixture = new AuditFixture();
        fixture.Elk();
        fixture.Docs();
        fixture.Npm("elkjs", "EPL-2.0", AuditFixture.Epl, "0.9.3", location: "docs/site");
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void NugetTransitiveAndNpmNestedPackages_AreCollected()
    {
        using var fixture = new AuditFixture();
        fixture.Nuget("nested", "MIT", AuditFixture.Mit);
        fixture.Npm("outer", "MIT", AuditFixture.Mit);
        fixture.Npm("outer/node_modules/nested", "MIT", AuditFixture.Mit);
        var report = fixture.Check();
        report.Errors.ShouldBeEmpty();
        report.Packages.ShouldContain(package => package.Ecosystem == "nuget" && package.Package == "nested");
        report.Packages.ShouldContain(package => package.Ecosystem == "npm" && package.Package == "nested");
    }

    [Fact]
    public void PythonWheel_ReadsMetadataAndLicenseOnly()
    {
        using var fixture = new AuditFixture();
        fixture.Python("honcho-client", "MIT", AuditFixture.Mit);
        var report = fixture.Check();
        report.Errors.ShouldBeEmpty();
        report.Packages.ShouldContain(package => package.Ecosystem == "python" && package.Package == "honcho-client");
    }

    [Fact]
    public void PythonUnpinnedRequirement_Fails()
    {
        using var fixture = new AuditFixture();
        fixture.WriteText("test/conformance/python/requirements.lock", "client>=1.0");
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void CopiedNugetContent_CannotBypassDepsClassification()
    {
        using var fixture = new AuditFixture();
        fixture.Nuget("epl", "EPL-2.0", AuditFixture.Epl, copiedContent: "unique test fixture copied third-party content");
        fixture.Exception("nuget", "epl", "1.0.0", "EPL-2.0");
        fixture.WriteText("artifacts/api/copied.txt", "unique test fixture copied third-party content");
        fixture.Check().Errors.ShouldContain(error => error.Contains("excepted package", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownCopiedContent_FailsClosed()
    {
        using var fixture = new AuditFixture();
        fixture.WriteText("artifacts/api/unattributed.js", "unattributed");
        fixture.Check().Errors.ShouldContain(error => error.Contains("provenance", StringComparison.Ordinal));
    }

    [Fact]
    public void CopiedContentSharedWithPermissivePackage_StillClassifiesException()
    {
        using var fixture = new AuditFixture();
        fixture.Nuget("first", "MIT", AuditFixture.Mit, "same copied content");
        fixture.Nuget("epl", "EPL-2.0", AuditFixture.Epl, "same copied content");
        fixture.Exception("nuget", "epl", "1.0.0", "EPL-2.0");
        fixture.WriteText("artifacts/api/copied.txt", "same copied content");
        fixture.Check().Errors.ShouldContain(error => error.Contains("excepted package", StringComparison.Ordinal));
    }

    [Fact]
    public void OverrideCannotDropAnAndObligation()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("laundered", "MIT AND EPL-2.0", AuditFixture.Mit);
        fixture.Override("npm", "laundered", "1.0.0", "MIT");
        fixture.Check().Errors.ShouldContain(error => error.Contains("AND", StringComparison.Ordinal));
    }

    [Fact]
    public void OverrideCannotImplicitlySelectAnOrLicense()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("dual", "MIT OR Apache-2.0", null);
        fixture.Override("npm", "dual", "1.0.0", "MIT");
        fixture.Check().Errors.ShouldContain(error => error.Contains("selection", StringComparison.Ordinal));
    }

    [Fact]
    public void RemovedMitNotice_IsNotAutomaticallyMitZero()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("damaged", "MIT-0", AuditFixture.Mit.Replace("\r\n", "\n", StringComparison.Ordinal).Replace(
            "The above copyright notice and this permission notice shall be included in all\ncopies or substantial portions of the Software.", "", StringComparison.Ordinal));
        fixture.Check().Errors.ShouldContain(error => error.Contains("unrecognized", StringComparison.Ordinal));
    }

    [Fact]
    public void BsdAdvertisingClause_IsNotPermissiveBsdTwoClause()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("bsd", "BSD-2-Clause", """
            Redistribution and use in source and binary forms
            Redistributions of source code must retain
            Redistributions in binary form must reproduce
            All advertising materials mentioning features or use of this software must display the following acknowledgement.
            This software is provided as is.
            """);
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void MissingNugetTransitiveInList_IsDetectedAgainstRestoreGraph()
    {
        using var fixture = new AuditFixture();
        fixture.Write("src/App/obj/project.assets.json", new
        {
            libraries = new Dictionary<string, object> { ["missing/1.0.0"] = new { type = "package" } }
        });
        fixture.Check().Errors.ShouldContain(error => error.Contains("restore graph", StringComparison.Ordinal));
    }

    [Fact]
    public void PythonSdist_ReadsActualLicense()
    {
        using var fixture = new AuditFixture();
        fixture.PythonSdist("legacy", "MIT", AuditFixture.Mit);
        var report = fixture.Check();
        report.Errors.ShouldBeEmpty();
        report.Packages.ShouldContain(package => package.Package == "legacy" && package.Ecosystem == "python");
    }

    [Fact]
    public void PythonDeclaredLicenseFile_CannotBeOmitted()
    {
        using var fixture = new AuditFixture();
        fixture.Python("client", "MIT", AuditFixture.Mit, "License-File: missing-notice.txt\n");
        fixture.Check().Errors.ShouldContain(error => error.Contains("License-File", StringComparison.Ordinal));
    }

    [Fact]
    public void PythonSdistLicenseFileWithNonstandardName_IsRead()
    {
        using var fixture = new AuditFixture();
        fixture.PythonSdist("legacy", "MIT", AuditFixture.Mit, "legal/terms.txt");
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Fact]
    public void PythonHashMismatch_Fails()
    {
        using var fixture = new AuditFixture();
        fixture.Python("client", "MIT", AuditFixture.Mit);
        fixture.WriteText("test/conformance/python/requirements.lock", "client==1.0.0 --hash=sha256:" + new string('0', 64));
        fixture.Check().Errors.ShouldContain(error => error.Contains("SHA256", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"version\":1,\"projects\":[]}")]
    [InlineData("null")]
    [InlineData("{}")]
    public void InvalidNugetInventory_FailsWithDiagnostic(string json)
    {
        using var fixture = new AuditFixture();
        fixture.WriteText("nuget.json", json);
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData(".github/scripts/pnpm-lock.yaml")]
    [InlineData("test/conformance/python/uv.lock")]
    public void UnsupportedLockfile_IsNotSilentlyIgnored(string path)
    {
        using var fixture = new AuditFixture();
        fixture.WriteText(path, "unsupported lock");
        fixture.Check().Errors.ShouldContain(error => error.Contains("Unsupported lock", StringComparison.Ordinal));
    }

    [Fact]
    public void NpmMissingLock_Fails()
    {
        using var fixture = new AuditFixture();
        File.Delete(fixture.Full(".github/scripts/package-lock.json"));
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void NpmTraversal_FailsWithoutReadingOutsidePackageRoot()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("unsafe", "SEE LICENSE IN ../../outside.txt", null);
        fixture.Check().Errors.ShouldContain(error => error.Contains("escapes evidence root", StringComparison.Ordinal));
    }

    [Fact]
    public void UnreviewedOverride_FailsClosed()
    {
        using var fixture = new AuditFixture();
        fixture.Write("eng/license-overrides.json", new[] { new { ecosystem = "npm", package = "baseline", version = "1.0.0", license = "MIT" } });
        fixture.Check().Errors.ShouldContain(error => error.Contains("unreviewed", StringComparison.Ordinal));
    }

    [Fact]
    public void EplSecondaryLicenseDefinition_IsNotAGplGrant()
    {
        using var fixture = new AuditFixture();
        fixture.Elk();
        AuditFixture.Epl.ShouldContain("Secondary License");
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Fact]
    public void EplAlongsideActualGplText_StillFails()
    {
        using var fixture = new AuditFixture();
        fixture.Elk();
        fixture.WriteText(".github/scripts/node_modules/elkjs/LICENSE", AuditFixture.Epl + "\n" + AuditFixture.Gpl);
        fixture.Check().Errors.ShouldContain(error => error.Contains("prohibited", StringComparison.Ordinal));
    }

    [Fact]
    public void ThirdPartyNotices_CannotHideProhibitedText()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("bad-notice", "MIT", AuditFixture.Mit);
        fixture.WriteText(".github/scripts/node_modules/bad-notice/THIRD-PARTY-NOTICES.txt", AuditFixture.Gpl);
        fixture.Check().Errors.ShouldContain(error => error.Contains("prohibited", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyGeneratedStaticAssetManifest_IsRecognized()
    {
        using var fixture = new AuditFixture();
        fixture.WriteText("artifacts/api/Nachos.Api.staticwebassets.endpoints.json", """{"Version":1,"ManifestType":"Publish","Endpoints":[]}""");
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Fact]
    public void NonemptyStaticAssetManifest_RequiresFurtherProvenance()
    {
        using var fixture = new AuditFixture();
        fixture.WriteText("artifacts/api/Nachos.Api.staticwebassets.endpoints.json", """{"Version":1,"ManifestType":"Publish","Endpoints":[{"Route":"unknown.js"}]}""");
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void GeneratedAspNetWebConfig_IsRecognized()
    {
        using var fixture = new AuditFixture();
        fixture.WriteText("artifacts/api/web.config", """
            <configuration><location path="." inheritInChildApplications="false"><system.webServer>
            <handlers><add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" /></handlers>
            <aspNetCore processPath="dotnet" arguments=".\Nachos.Api.dll" stdoutLogEnabled="false" stdoutLogFile=".\logs\stdout" hostingModel="inprocess" />
            </system.webServer></location></configuration>
            """);
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Fact]
    public void ArbitraryWebConfig_DoesNotGetGeneratedFileExemption()
    {
        using var fixture = new AuditFixture();
        fixture.WriteText("artifacts/api/web.config", "<configuration><unknown /></configuration>");
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void DeclaredMissingPythonText_CanUseReviewedEvidence()
    {
        using var fixture = new AuditFixture();
        fixture.Python("legacy", "MIT", null);
        fixture.Override("python", "legacy", "1.0.0", "MIT");
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Fact]
    public void DeclaredMissingNpmText_CanUseReviewedEvidence()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("legacy", "SEE LICENSE IN terms.txt", null);
        fixture.Override("npm", "legacy", "1.0.0", "MIT");
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Fact]
    public void CopiedPackageUnderFirstPartyName_RemainsDistributed()
    {
        using var fixture = new AuditFixture();
        fixture.Nuget("epl", "EPL-2.0", AuditFixture.Epl, "copied library bytes");
        fixture.Exception("nuget", "epl", "1.0.0", "EPL-2.0");
        fixture.WriteText("src/Nachos.Api/Nachos.Api.csproj", "<Project />");
        fixture.Write("artifacts/api/Nachos.Api.deps.json", new
        {
            libraries = new Dictionary<string, object> { ["Nachos.Api/1.0.0"] = new { type = "project" } }
        });
        fixture.WriteText("artifacts/api/Nachos.Api.dll", "copied library bytes");
        fixture.Check().Errors.ShouldContain(error => error.Contains("excepted package", StringComparison.Ordinal));
    }

    [Fact]
    public void SourceCodeUnderLicenseDirectory_IsNotReadAsLicenseEvidence()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("honcho-client", "MIT", AuditFixture.Mit);
        fixture.WriteText(".github/scripts/node_modules/honcho-client/licenses/implementation.py", "DO NOT READ SDK SOURCE");
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Fact]
    public void NullReviewRecord_FailsWithDiagnostic()
    {
        using var fixture = new AuditFixture();
        fixture.WriteText("eng/license-overrides.json", "[null]");
        fixture.Check().Errors.ShouldContain(error => error.Contains("unreviewed", StringComparison.Ordinal));
    }

    [Fact]
    public void CompatibilityStatement_IsNotACopyleftLicenseGrant()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("compatible", "MIT", AuditFixture.Mit + "\nThis license is GPL-compatible.");
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("SPDX-License-Identifier: LGPL-2.1-only")]
    [InlineData("This code is licensed under GPL.")]
    [InlineData("License: AGPL")]
    [InlineData("GPL License")]
    public void ProhibitedDeclaration_CannotHideBehindPermissiveText(string declaration)
    {
        using var fixture = new AuditFixture();
        fixture.Npm("hidden", "MIT", AuditFixture.Mit + "\n" + declaration);
        fixture.Override("npm", "hidden", "1.0.0", "MIT");
        fixture.Check().Errors.ShouldContain(error => error.Contains("prohibited", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyDepsJson_IsNotPublishedApplicationEvidence()
    {
        using var fixture = new AuditFixture();
        fixture.Write("artifacts/api/Nachos.Api.deps.json", new { libraries = new Dictionary<string, object>() });
        fixture.Check().Errors.ShouldContain(error => error.Contains("published application", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingExpectedToolingRoot_IsNotAnUnusedEcosystem()
    {
        using var fixture = new AuditFixture();
        Directory.Delete(fixture.Full(".github/scripts"), true);
        fixture.Check().Errors.ShouldContain(error => error.Contains("package-lock.json", StringComparison.Ordinal));
    }

    [Fact]
    public void VersionedCopyingFilename_IsNotSkipped()
    {
        using var fixture = new AuditFixture();
        fixture.WriteText(".github/scripts/node_modules/baseline/COPYING.LESSERv2.1", "GNU LESSER GENERAL PUBLIC LICENSE Version 2.1");
        fixture.Check().Errors.ShouldContain(error => error.Contains("prohibited", StringComparison.Ordinal));
    }

    [Fact]
    public void ImplementationExtensionOnImplicitLicenseCandidate_IsIgnoredWithoutReadingIt()
    {
        using var fixture = new AuditFixture();
        fixture.WriteText(".github/scripts/node_modules/baseline/LICENSE.py", "DO NOT READ SDK SOURCE");
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Fact]
    public void UnknownPrimaryLicense_IsNotRelicensedByMitNotice()
    {
        using var fixture = new AuditFixture();
        fixture.Nuget("vendor-runtime", "terms.txt", "Use is governed by a separate agreement with Example Vendor.",
            notice: AuditFixture.Mit, licenseType: "file");
        var report = fixture.Check();
        report.Errors.ShouldContain(error => error.Contains("vendor-runtime", StringComparison.Ordinal));
        report.Packages.ShouldNotContain(package => package.Package == "vendor-runtime");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProprietaryPrimaryTermsMixedWithMit_CannotPass(bool noticeFirst)
    {
        using var fixture = new AuditFixture();
        var text = noticeFirst ? AuditFixture.Mit + "\n" + AuditFixture.Proprietary
            : AuditFixture.Proprietary + "\n" + AuditFixture.Mit;
        fixture.Nuget("vendor-runtime", "terms.txt", text, licenseType: "file");
        fixture.Check().Errors.ShouldContain(error => error.Contains("vendor-runtime", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownPrimarySection_CannotBorrowRecognitionFromBundledNotice()
    {
        using var fixture = new AuditFixture();
        fixture.Nuget("vendor-runtime", "terms.txt",
            "Example Vendor Agreement\nUse requires separate authorization.\n\nTHIRD-PARTY NOTICES\n" + AuditFixture.Mit,
            licenseType: "file");
        fixture.Check().Errors.ShouldContain(error => error.Contains("vendor-runtime", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingDeclaredPrimaryLicense_CannotUseUnrelatedMitFile()
    {
        using var fixture = new AuditFixture();
        fixture.Nuget("vendor-runtime", "terms.txt", null, licenseType: "file");
        fixture.NugetEntry("vendor-runtime", "LICENSE-MIT.txt", AuditFixture.Mit);
        fixture.Check().Errors.ShouldContain(error => error.Contains("license text unavailable", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("MIT", "expression")]
    [InlineData("https://example.test/vendor-license", "url")]
    public void MitNoticeAlone_DoesNotEstablishPrimaryLicense(string license, string licenseType)
    {
        using var fixture = new AuditFixture();
        fixture.Nuget("vendor-runtime", license, null, notice: AuditFixture.Mit, licenseType: licenseType);
        fixture.Check().Errors.ShouldContain(error => error.Contains("primary license", StringComparison.Ordinal));
    }

    [Fact]
    public void MitWrapper_CannotRelicenseDependentVendorRuntime()
    {
        using var fixture = new AuditFixture();
        fixture.Nuget("build-wrapper", "MIT", AuditFixture.Mit);
        fixture.Nuget("vendor-runtime", "terms.txt", AuditFixture.Proprietary, notice: AuditFixture.Mit, licenseType: "file");
        fixture.Publish("vendor-runtime");
        var report = fixture.Check();
        report.Errors.ShouldContain(error => error.Contains("vendor-runtime", StringComparison.Ordinal));
        report.Packages.ShouldContain(package => package.Package == "build-wrapper");
        report.Packages.ShouldNotContain(package => package.Package == "vendor-runtime");
    }

    [Fact]
    public void NoticeOnlyDocument_IsNotPrimaryEvenWhenDeclaredAsLicenseFile()
    {
        using var fixture = new AuditFixture();
        fixture.Nuget("vendor-runtime", "terms.txt", "THIRD-PARTY NOTICES\n" + AuditFixture.Mit, licenseType: "file");
        fixture.Check().Errors.ShouldContain(error => error.Contains("primary license", StringComparison.Ordinal));
    }

    [Fact]
    public void MitPrimaryWithSupplementalMitNotice_Passes()
    {
        using var fixture = new AuditFixture();
        fixture.Nuget("open-runtime", "MIT", AuditFixture.Mit, notice: "THIRD-PARTY NOTICES\n" + AuditFixture.Mit);
        fixture.Check().Errors.ShouldBeEmpty();
    }
}

internal sealed class AuditFixture : IDisposable
{
    private static readonly string[] ExceptionReviewers = ["Cortado", "Cedar"];
    private static readonly string[] OverrideReviewers = ["Reviewer"];
    private readonly Dictionary<string, Dictionary<string, object>> npmLocks = new(StringComparer.Ordinal);
    private readonly List<object> nuget = [];
    private readonly Dictionary<string, object> assets = new(StringComparer.Ordinal);
    private readonly List<object> exceptions = [];

    public const string Mit = """
        MIT License
        Copyright (c) 2026 <copyright holders>
        Permission is hereby granted, free of charge, to any person obtaining a copy
        of this software and associated documentation files (the "Software"), to deal
        in the Software without restriction, including without limitation the rights
        to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
        copies of the Software, and to permit persons to whom the Software is
        furnished to do so, subject to the following conditions:
        The above copyright notice and this permission notice shall be included in all
        copies or substantial portions of the Software.
        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
        FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
        LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
        """;
    public static readonly string Apache = ReviewRegressionTests.CompleteLicense("Apache-2.0");
    public static readonly string Epl = ReviewRegressionTests.CompleteLicense("EPL-2.0");
    public static readonly string Isc = ReviewRegressionTests.CompleteLicense("ISC");
    public const string Gpl = "GNU GENERAL PUBLIC LICENSE\nVersion 3, 29 June 2007\nEveryone is permitted to copy and distribute verbatim copies of this license document.";
    public const string Proprietary = """
        EXAMPLE VENDOR SOFTWARE LICENSE TERMS
        EXAMPLE DATA ACCESS RUNTIME
        This software is licensed, not sold.
        Usage is restricted to separately authorized vendor products.
        Separate third-party notices do not grant rights to this runtime.
        """;

    public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "fixtures-work", Guid.NewGuid().ToString("N"));

    public AuditFixture()
    {
        Directory.CreateDirectory(Root);
        WriteText("eng/license-check/allowlist.json", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "allowlist.json")));
        Write("eng/license-exceptions.json", Array.Empty<object>());
        WriteText("Nachos.slnx", "<Solution><Project Path=\"src/App/App.csproj\" /></Solution>");
        WriteText("src/App/App.csproj", "<Project />");
        WriteText("src/Nachos.Api/Nachos.Api.csproj", "<Project />");
        WriteText("src/Nachos.Cli/Nachos.Cli.csproj", "<Project />");
        WriteText("artifacts/api/Nachos.Api.dll", "fixture application");
        WriteText("artifacts/cli/Nachos.Cli.dll", "fixture CLI");
        Inventory();
        Publish();
        Write("artifacts/cli/Nachos.Cli.deps.json", new { libraries = new Dictionary<string, object> { ["Nachos.Cli/1.0.0"] = new { type = "project" } } });
        Npm("baseline", "MIT", Mit);
    }

    public AuditReport Check() => LicenseAudit.Run(new AuditInputs(
        Root, Path.Combine(Root, "nuget.json"), Path.Combine(Root, "cache"),
        Path.Combine(Root, "artifacts", "api"), Path.Combine(Root, "artifacts", "cli"),
        Path.Combine(Root, "python-archives"), Path.Combine(Root, "npm-archives")));

    public void Npm(string name, string license, string? text, string version = "1.0.0", bool dev = false, string location = ".github/scripts")
    {
        var packageName = name.Split("/node_modules/", StringSplitOptions.None)[^1];
        if (!npmLocks.TryGetValue(location, out var packages))
        {
            packages = new Dictionary<string, object>(StringComparer.Ordinal) { [""] = new { name = "fixture", version = "1.0.0" } };
            npmLocks.Add(location, packages);
        }
        packages["node_modules/" + name] = new { version, license, dev };
        Write(location + "/package.json", new { name = "fixture", version = "1.0.0" });
        Write(location + "/package-lock.json", new { lockfileVersion = 3, packages });
        Write(location + "/node_modules/" + name + "/package.json", new { name = packageName, version, license });
        if (text is not null)
        {
            WriteText(location + "/node_modules/" + name + "/LICENSE", text);
        }
    }

    public void Elk()
    {
        Npm("elkjs", "EPL-2.0", Epl, "0.9.3");
        Exception("npm", "elkjs", "0.9.3", "EPL-2.0");
    }

    public void Exception(string ecosystem, string package, string version, string license)
    {
        exceptions.Add(new
        {
            ecosystem, package, version, license,
            purpose = "Mermaid syntax validation in .github/scripts only.",
            reviewers = ExceptionReviewers,
            review = "https://example.test/review",
            licenseEvidence = ecosystem == "nuget" ? Full($"cache/{package}/{version}/{package}.{version}.nupkg") + "!LICENSE.txt"
                : $".github/scripts/node_modules/{package}/LICENSE",
            restriction = "Unmodified, non-distributed developer/CI tool only."
        });
        Write("eng/license-exceptions.json", exceptions);
    }

    public void Override(string ecosystem, string package, string version, string license, string? selectedLicense = null) =>
        Write("eng/license-overrides.json", new[] { new
        {
            ecosystem, package, version, license, selectedLicense,
            evidenceUrl = "https://example.test/evidence", reviewers = OverrideReviewers, review = "https://example.test/review"
        } });

    public void Nuget(string name, string license, string? text, string? copiedContent = null, string? notice = null, string licenseType = "expression", string version = "1.0.0")
    {
        nuget.Add(new { id = name, resolvedVersion = version });
        assets.Add(name + "/" + version, new { type = "package" });
        Inventory();
        var lower = name.ToLowerInvariant();
        var cacheVersion = version.ToLowerInvariant();
        var path = Full($"cache/{lower}/{cacheVersion}/{lower}.{cacheVersion}.nupkg");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var licenseMetadata = licenseType == "url" ? $"<licenseUrl>{license}</licenseUrl>" : $"<license type=\"{licenseType}\">{license}</license>";
        Entry(zip, name + ".nuspec", $"<package><metadata><id>{name}</id><version>{version}</version>{licenseMetadata}</metadata></package>");
        if (text is not null)
        {
            Entry(zip, licenseType == "file" ? license : "LICENSE.txt", text);
        }
        if (copiedContent is not null)
        {
            Entry(zip, "content/copied.txt", copiedContent);
        }
        if (notice is not null)
        {
            Entry(zip, "THIRD-PARTY-NOTICES.txt", notice);
        }
    }

    public void NugetEntry(string name, string path, string text, string version = "1.0.0")
    {
        var lower = name.ToLowerInvariant();
        var cacheVersion = version.ToLowerInvariant();
        using var zip = ZipFile.Open(Full($"cache/{lower}/{cacheVersion}/{lower}.{cacheVersion}.nupkg"), ZipArchiveMode.Update);
        Entry(zip, path, text);
    }

    private void Inventory()
    {
        Write("nuget.json", new
        {
            version = 1, parameters = "--include-transitive",
            projects = new[] { new { path = Full("src/App/App.csproj"), frameworks = new[] { new { framework = "net10.0", transitivePackages = nuget } } } }
        });
        Write("src/App/obj/project.assets.json", new { libraries = assets });
    }

    public void Publish(string? package = null, string version = "1.0.0")
    {
        var libraries = new Dictionary<string, object> { ["Nachos.Api/1.0.0"] = new { type = "project" } };
        if (package is not null)
        {
            libraries.Add(package + "/" + version, new { type = "package" });
        }
        Write("artifacts/api/Nachos.Api.deps.json", new { libraries });
    }

    public void Docs(params object[] packages)
    {
        Npm("site-baseline", "MIT", Mit, location: "docs/site");
        Write("docs/site/dist/.nachos/bundle-modules.json", packages);
    }

    public void Python(string name, string license, string? text, string extraHeaders = "")
    {
        var path = Full($"python-archives/{name.Replace('-', '_')}-1.0.0-py3-none-any.whl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Entry(zip, name + "-1.0.0.dist-info/METADATA", $"Metadata-Version: 2.4\nName: {name}\nVersion: 1.0.0\nLicense-Expression: {license}\nLicense-File: licenses/LICENSE\n{extraHeaders}\n");
            if (text is not null)
            {
                Entry(zip, name + "-1.0.0.dist-info/licenses/LICENSE", text);
            }
            Entry(zip, name + "/implementation.py", "DO NOT READ IMPLEMENTATION SOURCE");
        }
        SealPythonArchive(name, path);
    }

    public void PythonSdist(string name, string license, string text, string licenseFile = "LICENSE", string? extraMetadata = null)
    {
        var path = Full($"python-archives/{name}-1.0.0.tar.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionMode.Compress))
        using (var tar = new TarWriter(gzip))
        {
            void Add(string entryName, string content)
            {
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"{name}-1.0.0/{entryName}") { DataStream = stream });
            }
            Add("PKG-INFO", $"Metadata-Version: 2.4\nName: {name}\nVersion: 1.0.0\nLicense-Expression: {license}\nLicense-File: {licenseFile}\n\n");
            Add(licenseFile, text);
            if (extraMetadata is not null)
            {
                Add(name + ".egg-info/PKG-INFO", extraMetadata);
            }
            Add("implementation.py", "DO NOT READ IMPLEMENTATION SOURCE");
        }
        SealPythonArchive(name, path);
    }

    public void SealPythonArchive(string name, string path)
    {
        using var stream = File.OpenRead(path);
        WriteText("test/conformance/python/requirements.lock",
            $"{name}==1.0.0 --hash=sha256:{Convert.ToHexString(SHA256.HashData(stream))}\n");
    }

    private static void Entry(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(text);
    }

    public void Write(string path, object value) => WriteText(path, JsonSerializer.Serialize(value));

    public void WriteText(string path, string text)
    {
        var full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    public string Full(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose() => Directory.Delete(Root, true);
}
