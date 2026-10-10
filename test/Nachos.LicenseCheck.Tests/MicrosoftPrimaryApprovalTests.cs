using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class MicrosoftPrimaryApprovalTests
{
    private const string Approval = "https://github.com/brendankowitz/nachos/pull/6#issuecomment-6083936795";
    private const string OwnerPath = "eng/licenses/microsoft-library-policy/owner-ms-license-6083936795.json";
    private const string OwnerHash = "7a843523fecd8769a984ed13ca94f6daa891e90c5dda873da504bb9694736753";

    [Theory]
    [InlineData("sni")]
    [InlineData("types")]
    [InlineData("sni603")]
    public void RepositoryRecordsAndRetainedSourceDocumentsBindTheActualOwnerDecision(string identity)
    {
        using var fixture = Fixture(identity);
        var record = Record(identity);
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var records = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "eng", "license-exceptions.json")))!.AsArray();
        records.Count(entry => entry!["package"]!.GetValue<string>() == Text(record, "package")
            && entry["version"]!.GetValue<string>() == Text(record, "version")).ShouldBe(1);
        File.Copy(Path.Combine(root, "eng", "license-exceptions.json"), fixture.Full("eng/license-exceptions.json"), true);
        foreach (var field in new[] { "licenseEvidence", "originEvidence", "ownerEvidence" })
        {
            var relative = Text(record, field);
            File.Copy(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), fixture.Full(relative), true);
        }
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("sni")]
    [InlineData("types")]
    [InlineData("sni603")]
    public void OwnerAcceptedTermsWithoutCopiesStillFailTheReleaseArtifactCheck(string identity)
    {
        using var fixture = Fixture(identity);
        File.Delete(fixture.Full("artifacts/api/" + Text(Record(identity), "licenseEvidence")));
        var report = fixture.Check();
        report.Errors.ShouldContain(error => error.Contains("Required Microsoft primary evidence or notice is missing", StringComparison.Ordinal));
        report.Packages.ShouldNotContain(package => package.Ecosystem == "nuget");
    }

    [Theory]
    [InlineData("sni", "api")]
    [InlineData("sni", "cli")]
    [InlineData("sni", "both")]
    [InlineData("types", "api")]
    [InlineData("types", "cli")]
    [InlineData("types", "both")]
    [InlineData("sni603", "api")]
    [InlineData("sni603", "cli")]
    [InlineData("sni603", "both")]
    public void GenuineArchivesOwnerEvidenceAndExactCopies_AcceptOnlyPrimaryEngineeringPolicy(string identity, string scope)
    {
        using var fixture = Fixture(identity, scope);
        var report = fixture.Check();
        report.Errors.ShouldBeEmpty();
        var record = Record(identity);
        var decision = report.Packages.Single(package => package.Package == Text(record, "package"));
        decision.Tier.ShouldBe("distributed");
        decision.SelectedLicense.ShouldBe(Text(record, "license"));
        decision.Evidence.ShouldContain(Approval);
        decision.Evidence.ShouldContain("sha256:" + Text(record, "licenseSha256"));
        decision.Evidence.ShouldContain("archive-sha256:" + Text(record, "archiveSha256"));
        decision.Evidence.ShouldContain("governing-terms:" + Text(record, "governingTerms"));
    }

    [Theory]
    [InlineData("approvalType", null)]
    [InlineData("approvalType", "dacfx")]
    [InlineData("approvalType", "unknown")]
    [InlineData("tier", null)]
    [InlineData("tier", "tooling")]
    [InlineData("ownerApproval", null)]
    [InlineData("ownerApproval", "https://example.test/approval")]
    [InlineData("review", "https://example.test/review")]
    [InlineData("ownerEvidence", "eng/other-owner.json")]
    [InlineData("ownerEvidenceSha256", "0")]
    [InlineData("package", "Microsoft.Fake")]
    [InlineData("version", "6.0.3")]
    [InlineData("ecosystem", "npm")]
    [InlineData("license", "MIT")]
    [InlineData("licenseEntry", "license.txt")]
    [InlineData("licenseEvidence", "eng/licenses/other.txt")]
    [InlineData("licenseSha256", "0")]
    [InlineData("archiveSha256", "0")]
    [InlineData("originEvidence", "eng/other.nuspec")]
    [InlineData("originSha256", "0")]
    [InlineData("evidenceUrl", "https://example.test/Microsoft")]
    [InlineData("governingTerms", null)]
    [InlineData("governingTerms", "production-rights-established")]
    [InlineData("selectedLicense", "MIT")]
    public void AdmissionRecordCannotBroadenIdentityAuthorityOrEvidence(string field, string? value)
    {
        using var fixture = new AuditFixture();
        var record = Record("sni");
        record[field] = value;
        fixture.Write("eng/license-exceptions.json", new[] { record });
        Reject(fixture);
    }

    [Theory]
    [InlineData("reviewers", "Cedar")]
    [InlineData("reviewers", "repository-owner")]
    [InlineData("artifactScopes", "docs")]
    [InlineData("artifactScopes", "api")]
    public void ArbitraryReviewerOrScope_IsNotTheActualOwnerDecision(string field, string value)
    {
        using var fixture = new AuditFixture();
        var record = Record("sni");
        record[field] = new JsonArray(value);
        fixture.Write("eng/license-exceptions.json", new[] { record });
        Reject(fixture);
    }

    [Theory]
    [InlineData("reviewers")]
    [InlineData("artifactScopes")]
    public void DuplicateOrMissingAuthorityAndScopeArraysFail(string field)
    {
        using var fixture = new AuditFixture();
        var record = Record("sni");
        record[field] = null;
        fixture.Write("eng/license-exceptions.json", new[] { record });
        Reject(fixture);
        record[field] = new JsonArray(field == "reviewers" ? "brendankowitz" : "api", field == "reviewers" ? "brendankowitz" : "api");
        fixture.Write("eng/license-exceptions.json", new[] { record });
        Reject(fixture);
    }

    [Fact]
    public void CopiedGenuineLicenseWithForgedPublisherMetadataIsNotReviewedOrigin()
    {
        using var fixture = Fixture("sni");
        var record = Record("sni");
        var archive = Archive(fixture, record);
        var entry = Text(record, "package") + ".nuspec";
        ReplaceEntry(archive, entry, Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(ReadEntry(archive, entry)).Replace("<authors>Microsoft</authors>", "<authors>Unknown Publisher</authors>", StringComparison.Ordinal)));
        Reject(fixture);
    }

    [Fact]
    public void InventoryVersionDriftCannotUseAnApprovedPackageName()
    {
        using var fixture = Fixture("sni");
        fixture.Nuget("Microsoft.Data.SqlClient.SNI.runtime", "LICENSE.txt", AuditFixture.Proprietary, licenseType: "file", version: "6.0.3");
        fixture.Publish("Microsoft.Data.SqlClient.SNI.runtime", "6.0.3");
        Reject(fixture);
    }

    [Theory]
    [InlineData("sni")]
    [InlineData("types")]
    [InlineData("sni603")]
    public void MissingOrDuplicateApproval_IsNotAuthorized(string identity)
    {
        using var fixture = Fixture(identity);
        fixture.Write("eng/license-exceptions.json", Array.Empty<object>());
        Reject(fixture);
        fixture.Write("eng/license-exceptions.json", new[] { Record(identity), Record(identity) });
        Reject(fixture);
    }

    [Theory]
    [InlineData("sni")]
    [InlineData("types")]
    [InlineData("sni603")]
    public void ApprovalCannotBeMovedIntoGenericOverrides(string identity)
    {
        using var fixture = Fixture(identity);
        fixture.Write("eng/license-exceptions.json", Array.Empty<object>());
        fixture.Write("eng/license-overrides.json", new[] { Record(identity) });
        Reject(fixture);
    }

    [Theory]
    [InlineData("sni")]
    [InlineData("types")]
    [InlineData("sni603")]
    public void GenericOverrideCannotRelabelApprovedPrimary(string identity)
    {
        using var fixture = Fixture(identity);
        var record = Record(identity);
        fixture.Override("nuget", Text(record, "package"), Text(record, "version"), "MIT");
        Reject(fixture);
    }

    [Theory]
    [InlineData("Microsoft.Fake", "Microsoft")]
    [InlineData("Unknown.Publisher", "Microsoft Corporation")]
    [InlineData("Microsoft.Data.SqlClient.SNI.runtime", "Microsoft")]
    public void PrefixOrAuthorAloneDoesNotAuthorizeVendorOrMissingPrimary(string package, string author)
    {
        using var fixture = new AuditFixture();
        fixture.Nuget(package, "MIT", null, notice: AuditFixture.Mit);
        ReplaceEntry(fixture.Full($"cache/{package.ToLowerInvariant()}/1.0.0/{package.ToLowerInvariant()}.1.0.0.nupkg"),
            package + ".nuspec", Encoding.UTF8.GetBytes(
                $"<package><metadata><id>{package}</id><version>1.0.0</version><authors>{author}</authors><license type=\"expression\">MIT</license></metadata></package>"));
        fixture.Publish(package);
        Reject(fixture);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("case-duplicate")]
    [InlineData("truncated")]
    [InlineData("appended")]
    [InlineData("bom")]
    public void ExactPrimaryEntryAndRawBytesAreRequired(string change)
    {
        using var fixture = Fixture("sni");
        var record = Record("sni");
        var path = Archive(fixture, record);
        var entry = Text(record, "licenseEntry");
        var original = ReadEntry(path, entry);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        if (change is not ("duplicate" or "case-duplicate")) { zip.GetEntry(entry)!.Delete(); }
        if (change != "missing")
        {
            using var stream = zip.CreateEntry(change == "case-duplicate" ? "license.txt" : entry).Open();
            stream.Write(change switch
            {
                "truncated" => original[..(original.Length / 2)],
                "appended" => original.Concat("\nAdditional license restriction."u8.ToArray()).ToArray(),
                "bom" => Encoding.UTF8.GetPreamble().Concat(original).ToArray(),
                _ => original
            });
        }
        zip.Dispose();
        Reject(fixture);
    }

    [Theory]
    [InlineData("licenseEvidence", false)]
    [InlineData("licenseEvidence", true)]
    [InlineData("originEvidence", false)]
    [InlineData("originEvidence", true)]
    [InlineData("ownerEvidence", false)]
    [InlineData("ownerEvidence", true)]
    public void SourceEvidenceCannotBeMissingOrReplaced(string field, bool tamper)
    {
        using var fixture = Fixture("sni");
        var path = fixture.Full(Text(Record("sni"), field));
        if (tamper) { File.AppendAllText(path, "\nChanged source and authority."); }
        else { File.Delete(path); }
        Reject(fixture);
    }

    [Theory]
    [InlineData("user", "login", "another-owner")]
    [InlineData(null, "body", "I do not approve this.")]
    [InlineData(null, "html_url", "https://example.test/approval")]
    public void ForgedOwnerEvidenceDoesNotReplaceCapturedApproval(string? parent, string field, string value)
    {
        using var fixture = Fixture("sni");
        var owner = JsonNode.Parse(File.ReadAllText(fixture.Full(OwnerPath)))!;
        (parent is null ? owner : owner[parent]!)[field] = value;
        fixture.Write(OwnerPath, owner);
        Reject(fixture);
    }

    [Theory]
    [InlineData("sni", "api", false)]
    [InlineData("sni", "cli", false)]
    [InlineData("sni", "cli", true)]
    [InlineData("types", "api", false)]
    [InlineData("types", "cli", false)]
    [InlineData("types", "api", true)]
    [InlineData("sni603", "api", false)]
    [InlineData("sni603", "cli", false)]
    [InlineData("sni603", "api", true)]
    public void EachShippingArtifactMustCarryTheExactPackageLicense(string identity, string scope, bool tamper)
    {
        using var fixture = Fixture(identity, "both");
        var path = fixture.Full("artifacts/" + scope + "/" + Text(Record(identity), "licenseEvidence"));
        if (tamper) { File.AppendAllText(path, "\nChanged copy."); }
        else { File.Delete(path); }
        Reject(fixture);
    }

    [Theory]
    [InlineData("sni")]
    [InlineData("types")]
    [InlineData("sni603")]
    public void WrongCopyDestinationDoesNotSatisfyNoticeContract(string identity)
    {
        using var fixture = Fixture(identity);
        var relative = Text(Record(identity), "licenseEvidence");
        File.Move(fixture.Full("artifacts/api/" + relative), fixture.Full("artifacts/api/" + Path.GetFileName(relative)));
        Reject(fixture);
    }

    [Theory]
    [InlineData("sni")]
    [InlineData("types")]
    [InlineData("sni603")]
    public void ChangingSourceAndPublishedLicenseTogetherDoesNotAuthorizeNewTerms(string identity)
    {
        using var fixture = Fixture(identity);
        var relative = Text(Record(identity), "licenseEvidence");
        File.AppendAllText(fixture.Full(relative), "\nRevised grant.");
        File.AppendAllText(fixture.Full("artifacts/api/" + relative), "\nRevised grant.");
        Reject(fixture);
    }

    [Theory]
    [InlineData("sni")]
    [InlineData("types")]
    [InlineData("sni603")]
    public void LicenseCopiesAloneDoNotProveDistribution(string identity)
    {
        using var fixture = Fixture(identity, "tooling");
        Reject(fixture);
    }

    [Theory]
    [InlineData("MIT")]
    [InlineData("LicenseRef-Microsoft-SqlClient-SNI-6.0.2 AND Apache-2.0")]
    [InlineData("LicenseRef-Microsoft-SqlClient-SNI-6.0.2 OR MIT")]
    [InlineData("GPL-3.0-only")]
    [InlineData("MIT AND LGPL-3.0-only")]
    public void ChangedMetadataObligationsOrSelectionsCannotUseApproval(string expression)
    {
        using var fixture = Fixture("sni");
        var record = Record("sni");
        var package = Text(record, "package");
        ReplaceEntry(Archive(fixture, record), package + ".nuspec", Encoding.UTF8.GetBytes(
            $"<package><metadata><id>{package}</id><version>6.0.2</version><license type=\"expression\">{expression}</license></metadata></package>"));
        Reject(fixture);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("GPL")]
    [InlineData("AGPL")]
    [InlineData("LGPL")]
    [InlineData("SSPL")]
    public void SupplementalThirdPartyTermsAreNeverWaived(string kind)
    {
        using var fixture = Fixture("sni");
        fixture.NugetEntry("Microsoft.Data.SqlClient.SNI.runtime", "THIRD-PARTY-NOTICES.txt",
            kind == "unknown" ? "Unrecognized supplemental requirements." : $"License: {kind}", "6.0.2");
        Reject(fixture);
    }

    [Fact]
    public void ArchiveRepackingCannotReuseIdenticalPrimaryAndNuspecEvidence()
    {
        using var fixture = Fixture("sni");
        fixture.NugetEntry("Microsoft.Data.SqlClient.SNI.runtime", "other.txt", "Unreviewed archive content.", "6.0.2");
        Reject(fixture);
    }

    [Fact]
    public void Types2022GoverningTermsCannotBeClaimedSupersededByOwnerPolicy()
    {
        using var fixture = new AuditFixture();
        var record = Record("types");
        record["governingTerms"] = "production-rights-established";
        fixture.Write("eng/license-exceptions.json", new[] { record });
        Reject(fixture);
    }

    [Fact]
    public void UnknownApprovalTypeIsRejectedEvenOnToolingRecords()
    {
        using var fixture = new AuditFixture();
        fixture.Elk();
        var records = JsonNode.Parse(File.ReadAllText(fixture.Full("eng/license-exceptions.json")))!.AsArray();
        records[0]!["approvalType"] = "microsoft-primary";
        fixture.Write("eng/license-exceptions.json", records);
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void IdenticalPrimaryLicenseDoesNotTransferHistorical602ApprovalTo603()
    {
        using var fixture = Fixture("sni603");
        fixture.Write("eng/license-exceptions.json", new[] { Record("sni") });
        Reject(fixture);
    }

    [Fact]
    public void Current603RecordDoesNotReplaceTheHistorical602Record()
    {
        using var fixture = Fixture("sni");
        fixture.Write("eng/license-exceptions.json", new[] { Record("sni603") });
        Reject(fixture);
    }

    [Theory]
    [InlineData("version", "6.0.2")]
    [InlineData("version", "6.0.4")]
    [InlineData("package", "Microsoft.Fake")]
    [InlineData("archiveSha256", "090b897e3658a11c5f734edc4b350d08e39dce6509bb4a75d09f96a5c1b6049d")]
    [InlineData("originSha256", "66232cc42f75a62d47f6662b6c1235b322bdd39ea5908a8a31a0fd7fd21f31c9")]
    [InlineData("licenseSha256", "0")]
    [InlineData("licenseEvidence", "eng/licenses/Microsoft.Data.SqlClient.SNI.runtime/6.0.2/LICENSE.txt")]
    [InlineData("originEvidence", "eng/licenses/Microsoft.Data.SqlClient.SNI.runtime/6.0.2/Microsoft.Data.SqlClient.SNI.runtime.nuspec")]
    [InlineData("ownerApproval", "https://example.test/not-the-owner")]
    [InlineData("ownerEvidenceSha256", "0")]
    [InlineData("approvalType", "unknown")]
    [InlineData("tier", "tooling")]
    [InlineData("selectedLicense", "MIT")]
    [InlineData("licenseEntry", "license.txt")]
    public void Current603RecordCannotBorrowIdentityOrAuthority(string field, string value)
    {
        using var fixture = new AuditFixture();
        var record = Record("sni603");
        record[field] = value;
        fixture.Write("eng/license-exceptions.json", new[] { record });
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Current603ScopeCannotExpandToDocs()
    {
        using var fixture = new AuditFixture();
        var record = Record("sni603");
        record["artifactScopes"] = new JsonArray("api", "docs");
        fixture.Write("eng/license-exceptions.json", new[] { record });
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("licenseEvidence")]
    [InlineData("originEvidence")]
    [InlineData("ownerEvidence")]
    public void Current603SourceEvidenceMustRemainExact(string field)
    {
        using var fixture = Fixture("sni603");
        File.AppendAllText(fixture.Full(Text(Record("sni603"), field)), "\nChanged evidence.");
        Reject(fixture);
    }

    [Fact]
    public void Current603ArchiveCannotBeRepackedDespiteIdenticalLicenseAndNuspec()
    {
        using var fixture = Fixture("sni603");
        fixture.NugetEntry("Microsoft.Data.SqlClient.SNI.runtime", "extra.txt", "Unreviewed extra payload.", "6.0.3");
        Reject(fixture);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("GPL")]
    [InlineData("LGPL")]
    public void Current603SupplementalTermsCannotBeWaived(string kind)
    {
        using var fixture = Fixture("sni603");
        fixture.NugetEntry("Microsoft.Data.SqlClient.SNI.runtime", "THIRD-PARTY-NOTICES.txt",
            kind == "unknown" ? "Additional unknown terms." : $"License: {kind}", "6.0.3");
        Reject(fixture);
    }

    private static void Reject(AuditFixture fixture)
    {
        var report = fixture.Check();
        report.Errors.ShouldNotBeEmpty();
        report.Packages.ShouldNotContain(package => package.Ecosystem == "nuget");
    }

    [Theory]
    [InlineData("sni")]
    [InlineData("sni603")]
    [InlineData("types")]
    public void BuildMetadataEquivalenceDoesNotBroadenExactArchiveApproval(string identity)
    {
        using var fixture = Fixture(identity);
        var record = Record(identity);
        var archive = Archive(fixture, record);
        var entry = Text(record, "package") + ".nuspec";
        var version = Text(record, "version");
        var raw = Encoding.UTF8.GetString(ReadEntry(archive, entry));
        var modified = raw.Replace($"<version>{version}</version>", $"<version>{version}+build</version>", StringComparison.Ordinal);
        modified.ShouldNotBe(raw);
        ReplaceEntry(archive, entry, Encoding.UTF8.GetBytes(modified));
        var errors = new List<string>();
        Nachos.LicenseCheck.Collectors.Collect(Apache2DocumentTests.Inputs(fixture), errors)
            .ShouldContain(package => package.Name == Text(record, "package"));
        errors.ShouldBeEmpty();
        var report = fixture.Check();
        report.Packages.ShouldNotContain(package => package.Ecosystem == "nuget");
        report.Errors.ShouldContain(error => error.Contains("Microsoft primary archive SHA256 differs from the reviewed package", StringComparison.Ordinal));
    }

    private static AuditFixture Fixture(string identity, string scope = "api")
    {
        var fixture = new AuditFixture();
        var record = Record(identity);
        var package = Text(record, "package");
        var version = Text(record, "version");
        fixture.Nuget(package, Text(record, "licenseEntry"), null, licenseType: "file", version: version);
        var source = Path.Combine(AppContext.BaseDirectory, "MicrosoftPrimaryEvidence");
        var original = Path.Combine(source, package.ToLowerInvariant() + "." + version + ".nupkg");
        using (var stream = File.OpenRead(original))
        {
            Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant().ShouldBe(Text(record, "archiveSha256"));
        }
        File.Copy(original, Archive(fixture, record), true);
        CopyBytes(fixture.Full(Text(record, "licenseEvidence")), ReadEntry(original, Text(record, "licenseEntry")));
        CopyBytes(fixture.Full(Text(record, "originEvidence")), ReadEntry(original, package + ".nuspec"));
        var owner = File.ReadAllBytes(Path.Combine(source, "owner-ms-license-6083936795.json"));
        Convert.ToHexString(SHA256.HashData(owner)).ToLowerInvariant().ShouldBe(OwnerHash);
        CopyBytes(fixture.Full(OwnerPath), owner);
        fixture.Write("eng/license-exceptions.json", new[] { record });
        foreach (var target in new[] { "api", "cli" })
        {
            var relative = Text(record, "licenseEvidence");
            CopyBytes(fixture.Full("artifacts/" + target + "/" + relative), File.ReadAllBytes(fixture.Full(relative)));
            if (scope == target || scope == "both")
            {
                var application = target == "api" ? "Nachos.Api" : "Nachos.Cli";
                fixture.Write("artifacts/" + target + "/" + application + ".deps.json", new
                {
                    libraries = new Dictionary<string, object>
                    {
                        [application + "/1.0.0"] = new { type = "project" },
                        [package + "/" + version] = new { type = "package" }
                    }
                });
            }
        }
        return fixture;
    }

    private static JsonObject Record(string identity)
    {
        var sni = identity is "sni" or "sni603";
        var package = sni ? "Microsoft.Data.SqlClient.SNI.runtime" : "Microsoft.SqlServer.Types";
        var version = identity == "sni603" ? "6.0.3" : sni ? "6.0.2" : "170.1000.7";
        var entry = sni ? "LICENSE.txt" : "license.md";
        var directory = $"eng/licenses/{package}/{version}/";
        return new JsonObject
        {
            ["ecosystem"] = "nuget", ["package"] = package, ["version"] = version,
            ["approvalType"] = "microsoft-primary", ["tier"] = "shipped",
            ["license"] = sni ? $"LicenseRef-Microsoft-SqlClient-SNI-{version}" : "LicenseRef-Microsoft-SQL-Server-Types-170.1000.7",
            ["artifactScopes"] = new JsonArray("api", "cli"),
            ["reviewers"] = new JsonArray("brendankowitz"), ["review"] = Approval, ["ownerApproval"] = Approval,
            ["ownerEvidence"] = OwnerPath, ["ownerEvidenceSha256"] = OwnerHash,
            ["evidenceUrl"] = $"https://www.nuget.org/packages/{package}/{version}/License",
            ["licenseEntry"] = entry, ["licenseEvidence"] = directory + entry,
            ["originEvidence"] = directory + package + ".nuspec",
            ["licenseSha256"] = sni ? "9335e8bad875dd7be4eebd55d2335eb6433d1cea61aadb3817af7807bef8932a" : "e4b4088d14de78a57d485d0bc53f3250f3e1d2376993ddb2c5b165eca3d59d40",
            ["originSha256"] = identity == "sni603" ? "d5384233109efc8ca42e51d7e1f7f3d35d47eb5a878843e3002c77550babcd82"
                : sni ? "66232cc42f75a62d47f6662b6c1235b322bdd39ea5908a8a31a0fd7fd21f31c9" : "cfdc24005bcba7ba5aff56c8fe146e10e5f9d39b526e248b56cd8f703bb6ba99",
            ["archiveSha256"] = identity == "sni603" ? "b9df07c20101398f77cf16b209afefafcc7190d6ac0b6e244e81a2fed4c96f5f"
                : sni ? "090b897e3658a11c5f734edc4b350d08e39dce6509bb4a75d09f96a5c1b6049d" : "cf5a138692bd7683a971d030013eda563cd54ce472f52ffd62e96350ff1f9970",
            ["governingTerms"] = sni ? "distributable-code-conditions" : "unresolved-pre-release-2022",
            ["purpose"] = "Owner-accepted primary library engineering policy for API/CLI.",
            ["restriction"] = "Policy acceptance only; publisher terms and third-party obligations remain release-owner gates."
        };
    }

    private static string Text(JsonObject record, string property) => record[property]!.GetValue<string>();

    private static string Archive(AuditFixture fixture, JsonObject record)
    {
        var package = Text(record, "package").ToLowerInvariant();
        var version = Text(record, "version");
        return fixture.Full($"cache/{package}/{version}/{package}.{version}.nupkg");
    }

    private static byte[] ReadEntry(string archive, string entry)
    {
        using var zip = ZipFile.OpenRead(archive);
        using var stream = zip.GetEntry(entry)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void ReplaceEntry(string archive, string entry, byte[] bytes)
    {
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Update);
        zip.GetEntry(entry)!.Delete();
        using var stream = zip.CreateEntry(entry).Open();
        stream.Write(bytes);
    }

    private static void CopyBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
}
