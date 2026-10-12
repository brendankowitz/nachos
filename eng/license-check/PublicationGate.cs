using System.Text.Json;
using System.Security.Cryptography;

namespace Nachos.LicenseCheck;

public sealed record PublicationGateInputs(string RepoRoot, string LicenseReport, string DocsReport, string SiteOutput);
public sealed record PublicationGateResult(int ExitCode, IReadOnlyList<string> Messages);

public static class PublicationGate
{
    public static PublicationGateResult Verify(PublicationGateInputs inputs)
    {
        var messages = new List<string>();
        var report = JsonSerializer.Deserialize<AuditReport>(File.ReadAllText(inputs.LicenseReport), JsonOptions.CaseInsensitive)
            ?? throw new InvalidDataException("Invalid license report.");
        var docsReport = JsonSerializer.Deserialize<DocsProvenanceReport>(File.ReadAllText(inputs.DocsReport), JsonOptions.CaseInsensitive)
            ?? throw new InvalidDataException("Invalid docs provenance report.");
        var allowlist = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(inputs.RepoRoot, "eng", "license-check", "allowlist.json")), JsonOptions.CaseInsensitive)
            ?? throw new InvalidDataException("Missing allowlist.");
        var allowed = allowlist.ToHashSet(StringComparer.Ordinal);

        if (report.DocsProvenance is null)
        {
            messages.Add("publication gate failed: missing docsProvenance in full audit report.");
        }
        else if (!string.Equals(report.DocsProvenance.ManifestSha256, docsReport.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            messages.Add("publication gate failed: docsProvenance manifestSha256 mismatch with standalone verify-docs report.");
        }

        if (report.DocsProvenance is not null)
        {
            foreach (var package in report.DocsProvenance.Packages.OrderBy(package => package.Package, StringComparer.Ordinal).ThenBy(package => package.Version, StringComparer.Ordinal))
            {
                var id = PackageEvidence.Identity("npm", package.Package, package.Version);
                var decisions = report.Packages.Where(decision => decision.Ecosystem == "npm"
                    && decision.Package == package.Package && decision.Version == package.Version).ToArray();
                if (decisions.Length != 1)
                {
                    messages.Add($"publication gate failed: missing accepted distributed decision for {id} (found {decisions.Length}).");
                    continue;
                }
                var decision = decisions[0];
                if (decision.Tier != "distributed")
                {
                    messages.Add($"publication gate failed: {id} decision must be distributed, not {decision.Tier}.");
                }
                var licenses = decision.SelectedLicense.Split(" AND ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var license in licenses)
                {
                    if (!allowed.Contains(license))
                    {
                        messages.Add($"publication gate failed: {id} selected license {license} is not in the allowlist.");
                    }
                }
                if (AttributedErrors(report, id).Any())
                {
                    messages.Add($"publication gate failed: audit error attributed to emitted package {id}.");
                }
            }
        }

        foreach (var error in report.InputErrors.Where(error => error.Stage == "provenance" && ConcernsDocs(error.Message)))
        {
            messages.Add($"publication gate failed: docs provenance input error remains: {error.Message}");
        }

        var noticesPath = Path.Combine(inputs.SiteOutput, "THIRD-PARTY-NOTICES.txt");
        if (!File.Exists(noticesPath))
        {
            messages.Add("publication gate failed: missing THIRD-PARTY-NOTICES.txt in site output.");
        }
        else
        {
            var notices = NormalizeNotice(File.ReadAllText(noticesPath));
            if (report.DocsProvenance is not null)
            {
                foreach (var package in report.DocsProvenance.Packages)
                {
                    var text = package.Package + "@" + package.Version;
                    if (!notices.Contains(text, StringComparison.Ordinal))
                    {
                        messages.Add($"publication gate failed: notices missing {text}.");
                    }
                    foreach (var license in PackageLicenseTexts(inputs.RepoRoot, package))
                    {
                        RequireNoticeText(messages, notices, text, license);
                    }
                }
            }
            foreach (var component in EmbeddedComponents(inputs.RepoRoot))
            {
                var text = component.Component + "@" + component.Version;
                if (!notices.Contains(text, StringComparison.Ordinal))
                {
                    messages.Add($"publication gate failed: notices missing embedded component {text}.");
                }
                var source = Path.Combine(inputs.RepoRoot, component.Path.Replace('/', Path.DirectorySeparatorChar));
                var expected = ReadNormalized(source);
                var hash = Hash(expected);
                if (!string.Equals(component.Sha256, hash, StringComparison.OrdinalIgnoreCase))
                {
                    messages.Add($"publication gate failed: evidence hash mismatch for embedded component {text}.");
                }
                RequireNoticeText(messages, notices, text, expected);
            }
        }

        messages.Add(report.Errors.Count == 0
            ? "publication gate: full audit is green (0 errors remain)."
            : $"publication gate: full audit is NOT green: {report.Errors.Count} errors remain outside the emitted docs publication gate.");
        if (messages.Any(message => message.StartsWith("publication gate failed:", StringComparison.Ordinal)))
        {
            return new PublicationGateResult(1, messages);
        }
        messages.Insert(0, "publication gate passed for emitted documentation site artifact.");
        return new PublicationGateResult(0, messages);
    }

    private static IEnumerable<string> AttributedErrors(AuditReport report, string identity) =>
        report.Errors.Concat(report.InputErrors.Select(error => error.Message))
            .Where(error => error.Contains(identity, StringComparison.Ordinal));

    private static bool ConcernsDocs(string message) =>
        message.Contains("docs/site", StringComparison.OrdinalIgnoreCase)
        || message.Contains("docs output provenance", StringComparison.OrdinalIgnoreCase)
        || message.Contains("output-provenance", StringComparison.OrdinalIgnoreCase)
        || message.Contains("bundle-modules", StringComparison.OrdinalIgnoreCase)
        || message.Contains("Docs provenance", StringComparison.Ordinal);

    private static string[] PackageLicenseTexts(string repoRoot, DocsPackage package)
    {
        var directory = Path.Combine(repoRoot, "docs", "site", "node_modules", package.Package.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(directory))
        {
            if (package.Package is "pagefind" or "@pagefind/default-ui" or "@pagefind/linux-x64" or "@pagefind/windows-x64")
            {
                return EmbeddedComponents(repoRoot).Where(record => record.Component == "Pagefind" && record.Version == package.Version)
                    .Select(record => ReadNormalized(Path.Combine(repoRoot, record.Path.Replace('/', Path.DirectorySeparatorChar))))
                    .ToArray();
            }
            return [];
        }
        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(file => LicenseName(Path.GetFileName(file)))
            .Order(StringComparer.Ordinal)
            .Select(ReadNormalized)
            .ToArray();
    }

    private static void RequireNoticeText(List<string> messages, string notices, string identity, string expected)
    {
        if (!notices.Contains("SHA-256: " + Hash(expected), StringComparison.Ordinal) || !notices.Contains(expected, StringComparison.Ordinal))
        {
            messages.Add($"publication gate failed: notices missing license text for {identity}.");
        }
    }

    private static string ReadNormalized(string path) => NormalizeNotice(File.ReadAllText(path));

    private static string NormalizeNotice(string text) => text.ReplaceLineEndings("\n").TrimEnd() + "\n";

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private static bool LicenseName(string name) => name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("LICENCE", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("NOTICE", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("COPYRIGHT", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "UNLICENSE", StringComparison.OrdinalIgnoreCase);

    private static EmbeddedComponent[] EmbeddedComponents(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "docs", "site", "notices", "evidence-manifest.json");
        if (!File.Exists(path))
        {
            return [];
        }
        var records = JsonSerializer.Deserialize<EmbeddedComponent[]>(File.ReadAllText(path), JsonOptions.CaseInsensitive)
            ?? throw new InvalidDataException("Invalid embedded notice evidence manifest.");
        return records.DistinctBy(record => (record.Component, record.Version)).ToArray();
    }

    private sealed record EmbeddedComponent(string Component, string Version, string Path, string Sha256);
}
