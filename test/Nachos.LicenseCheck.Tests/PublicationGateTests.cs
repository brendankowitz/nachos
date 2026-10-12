using System.Text.Json;
using Nachos.LicenseCheck;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class PublicationGateTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "publication-gate", Guid.NewGuid().ToString("N"));
    private readonly DocsProvenanceReport docs = new(1, 1, 1, [new DocsPackage("astro", "7.3.5")], new string('a', 64));

    public PublicationGateTests()
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Full("eng/license-check"));
        File.WriteAllText(Full("eng/license-check/allowlist.json"), "[\"MIT\"]");
        Write(Full("docs-report.json"), docs);
        Directory.CreateDirectory(Full("dist"));
        Directory.CreateDirectory(Full("docs/site/node_modules/astro"));
        File.WriteAllText(Full("docs/site/node_modules/astro/package.json"), """{"name":"astro","version":"7.3.5"}""");
        File.WriteAllText(Full("docs/site/node_modules/astro/LICENSE"), "fixture astro license text\n");
        File.WriteAllText(Full("dist/THIRD-PARTY-NOTICES.txt"), """
            ## astro@7.3.5
            SHA-256: B8469086899CC3B0047C20F389B8782A1E3E6F0D62C3F0A37D03EB06E7B9FBA9

            fixture astro license text

            ## Svelte@4.2.20
            SHA-256: A403CA9420B1C0132E30DE1812B18F8D6AD07C9C20B5A0BA7260218CBCF8DDF1

            fixture svelte license text
            """.ReplaceLineEndings("\n"));
        Directory.CreateDirectory(Full("docs/site/notices"));
        File.WriteAllText(Full("docs/site/notices/svelte.txt"), "fixture svelte license text\n");
        Write(Full("docs/site/notices/evidence-manifest.json"), new[] { new
        {
            component = "Svelte", version = "4.2.20", path = "docs/site/notices/svelte.txt",
            sha256 = "A403CA9420B1C0132E30DE1812B18F8D6AD07C9C20B5A0BA7260218CBCF8DDF1"
        } });
    }

    [Fact]
    public void PositiveGatePassesAndSummarizesRemainingAuditErrors()
    {
        WriteReport(errors: ["baseline API error"]);
        var result = Verify();
        result.ExitCode.ShouldBe(0, string.Join("\n", result.Messages));
        result.Messages.ShouldContain(message => message.Contains("full audit is NOT green: 1 errors remain", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingAcceptedDecisionFails()
    {
        WriteReport(packages: []);
        Verify().Messages.ShouldContain(message => message.Contains("missing accepted distributed decision for npm:astro@7.3.5", StringComparison.Ordinal));
    }

    [Fact]
    public void ToolingTierDecisionFails()
    {
        WriteReport(packages: [Decision(tier: "tooling")]);
        Verify().Messages.ShouldContain(message => message.Contains("must be distributed", StringComparison.Ordinal));
    }

    [Fact]
    public void ErrorAttributedToEmittedPackageFails()
    {
        WriteReport(errors: ["npm:astro@7.3.5 (docs/site/package-lock.json): unrecognized license text"]);
        Verify().Messages.ShouldContain(message => message.Contains("audit error attributed to emitted package npm:astro@7.3.5", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingDocsProvenanceFails()
    {
        Write(Full("license-report.json"), new AuditReport([Decision()], []));
        Verify().Messages.ShouldContain(message => message.Contains("missing docsProvenance", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingNoticesFileFails()
    {
        WriteReport();
        File.Delete(Full("dist/THIRD-PARTY-NOTICES.txt"));
        Verify().Messages.ShouldContain(message => message.Contains("missing THIRD-PARTY-NOTICES.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void NoticesMissingIdentityFails()
    {
        WriteReport();
        File.WriteAllText(Full("dist/THIRD-PARTY-NOTICES.txt"), "## Svelte@4.2.20\n");
        Verify().Messages.ShouldContain(message => message.Contains("notices missing astro@7.3.5", StringComparison.Ordinal));
    }

    [Fact]
    public void NoticesWithOnlyHeadingsFail()
    {
        WriteReport();
        File.WriteAllText(Full("dist/THIRD-PARTY-NOTICES.txt"), "## astro@7.3.5\n## Svelte@4.2.20\n");
        Verify().Messages.ShouldContain(message => message.Contains("notices missing license text for astro@7.3.5", StringComparison.Ordinal));
    }

    [Fact]
    public void ManifestMismatchFails()
    {
        WriteReport(docs with { ManifestSha256 = new string('b', 64) });
        Verify().Messages.ShouldContain(message => message.Contains("manifestSha256 mismatch", StringComparison.Ordinal));
    }

    private PublicationGateResult Verify() => PublicationGate.Verify(new PublicationGateInputs(
        root, Full("license-report.json"), Full("docs-report.json"), Full("dist")));

    private void WriteReport(DocsProvenanceReport? provenance = null, IReadOnlyList<PackageDecision>? packages = null,
        IReadOnlyList<string>? errors = null, IReadOnlyList<AuditInputError>? inputErrors = null) =>
        Write(Full("license-report.json"), new AuditReport(packages ?? [Decision()], errors ?? [])
        {
            DocsProvenance = provenance ?? docs,
            InputErrors = inputErrors ?? []
        });

    private static PackageDecision Decision(string tier = "distributed", string selectedLicense = "MIT") =>
        new("npm", "astro", "7.3.5", tier, selectedLicense, []);

    private static void Write(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions.CaseInsensitive));
    }

    private string Full(string path) => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose() => Directory.Delete(root, true);
}

