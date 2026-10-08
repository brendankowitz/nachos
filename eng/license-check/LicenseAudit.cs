using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nachos.LicenseCheck;

public sealed record AuditInputs(string Root, string NugetInventory, string NugetCache, string ApiPublish, string CliPublish, string? PythonArchives = null, string? NpmArchives = null);
public sealed record PackageDecision(string Ecosystem, string Package, string Version, string Tier, string SelectedLicense, IReadOnlyList<string> Evidence);
public sealed record AuditReport(IReadOnlyList<PackageDecision> Packages, IReadOnlyList<string> Errors);

public static class LicenseAudit
{
    public static AuditReport Run(AuditInputs inputs)
    {
        var errors = new List<string>();
        var decisions = new List<PackageDecision>();
        try
        {
            var allowed = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(inputs.Root, "eng", "license-check", "allowlist.json")))
                ?? throw new InvalidDataException("Missing allowlist.");
            var exceptions = Reviews.Read(Path.Combine(inputs.Root, "eng", "license-exceptions.json"), required: true);
            var overrides = Reviews.Read(Path.Combine(inputs.Root, "eng", "license-overrides.json"), required: false);
            var packages = Collectors.Collect(inputs, errors);
            var shipped = Artifacts.Collect(inputs, packages, errors);
            foreach (var package in packages)
            {
                try
                {
                    decisions.Add(Evaluate(inputs, package, shipped.GetValueOrDefault(package.Key) ?? [], allowed, exceptions, overrides));
                }
                catch (InvalidDataException exception)
                {
                    errors.Add($"{package.Ecosystem}:{package.Name}@{package.Version} ({package.Origin}): {exception.Message}");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            errors.Add(exception.Message);
        }
        return new AuditReport(decisions, errors);
    }

    private static PackageDecision Evaluate(AuditInputs inputs, PackageEvidence package, HashSet<string> scopes,
        string[] allowed, Reviews[] exceptions, Reviews[] overrides)
    {
        var distributed = scopes.Count > 0;
        var review = overrides.SingleOrDefault(entry => entry.Matches(package));
        var exception = exceptions.SingleOrDefault(entry => entry.Matches(package));
        if (distributed && exception is not null && exception.Tier != "shipped")
        {
            throw new InvalidDataException("excepted package appears in a distributed artifact.");
        }
        var texts = package.Texts.Select(text => LicenseText.Identify(text.Text)).ToArray();
        if (LicenseText.Prohibited(package.Metadata ?? "") || texts.Any(text => text.Prohibited))
        {
            throw new InvalidDataException("prohibited GPL/AGPL/LGPL/SSPL license in metadata or observed text; overrides cannot relabel it.");
        }
        var approvedDacFx = exception?.Tier == "shipped";
        if (approvedDacFx)
        {
            if (review is not null)
            {
                throw new InvalidDataException("The exact shipped DacFx approval cannot be combined with a generic license-evidence override.");
            }
            var primary = DacFxApproval.Authorize(inputs, package, scopes);
            texts[primary] = (new HashSet<string>([DacFxApproval.License], StringComparer.Ordinal), false);
        }
        var observed = texts.Where((_, index) => package.Texts[index].IsPrimary && !LicenseText.IsNoticeDocument(package.Texts[index].Text))
            .SelectMany(text => text.Licenses).ToHashSet(StringComparer.Ordinal);
        var supplemental = texts.Where((_, index) => !package.Texts[index].IsPrimary || LicenseText.IsNoticeDocument(package.Texts[index].Text))
            .SelectMany(text => text.Licenses).ToHashSet(StringComparer.Ordinal);
        if (texts.Where((_, index) => !package.Texts[index].IsPrimary || LicenseText.IsNoticeDocument(package.Texts[index].Text))
            .Any(text => text.Licenses.Count == 0))
        {
            throw new InvalidDataException("unrecognized supplemental license text; a primary-license override cannot discard it. "
                + string.Join(", ", package.Texts.Where((file, index) => (!file.IsPrimary || LicenseText.IsNoticeDocument(file.Text))
                    && texts[index].Licenses.Count == 0).Select(file => file.Path)));
        }
        var metadata = string.IsNullOrWhiteSpace(package.Metadata) ? null : Spdx.Parse(package.Metadata);
        var expression = review is null ? metadata : Spdx.Parse(review.License);
        if (metadata?.HasChoice == true && string.IsNullOrWhiteSpace(review?.SelectedLicense))
        {
            throw new InvalidDataException("SPDX OR metadata requires a recorded explicit selection, including when evidence is overridden.");
        }
        if (review is not null && metadata is { HasChoice: false } && metadata.AllLicenses.Count > 1
            && !expression!.AllLicenses.IsSupersetOf(metadata.AllLicenses))
        {
            throw new InvalidDataException("override cannot discard a declared AND obligation.");
        }
        if (metadata?.HasChoice == true && review?.SelectedLicense is not null)
        {
            var choice = Spdx.Parse(review.SelectedLicense);
            if (choice.HasChoice || !metadata.Alternatives.Any(branch => branch.SetEquals(choice.AllLicenses)))
            {
                throw new InvalidDataException("recorded selection is not a complete metadata SPDX branch.");
            }
        }
        if (review is not null && observed.Any(license => !expression!.AllLicenses.Contains(license)))
        {
            throw new InvalidDataException("override cannot discard an observed license component.");
        }
        if (review is null)
        {
            if (texts.Length == 0 || package.Texts.Any(file => string.IsNullOrWhiteSpace(file.Text)))
            {
                throw new InvalidDataException("license text unavailable; a reviewed exact-version evidence override is required. "
                    + string.Join(", ", package.Texts.Where(file => string.IsNullOrWhiteSpace(file.Text)).Select(file => file.Path)));
            }
            if (texts.Any(text => text.Licenses.Count == 0))
            {
                throw new InvalidDataException("unrecognized license text; metadata alone is not evidence. "
                    + string.Join(", ", package.Texts.Where((_, index) => texts[index].Licenses.Count == 0).Select(file => file.Path)));
            }
            if (!package.Texts.Any(file => file.IsPrimary && !LicenseText.IsNoticeDocument(file.Text)))
            {
                throw new InvalidDataException("primary license text unavailable; supplemental notices cannot establish the package's license.");
            }
            if (metadata is not null && !metadata.AllLicenses.SetEquals(observed))
            {
                throw new InvalidDataException($"metadata and license text disagree: declared [{package.Metadata}], observed [{string.Join(" AND ", observed.Order(StringComparer.Ordinal))}].");
            }
        }
        expression ??= Spdx.Parse(string.Join(" AND ", observed.Order(StringComparer.Ordinal)));
        if (expression.AllLicenses.Any(LicenseText.Prohibited))
        {
            throw new InvalidDataException("prohibited license in evidence override.");
        }
        HashSet<string> selected;
        if (expression.HasChoice)
        {
            if (string.IsNullOrWhiteSpace(review?.SelectedLicense))
            {
                throw new InvalidDataException("SPDX OR requires a recorded explicit selection.");
            }
            var choice = Spdx.Parse(review.SelectedLicense);
            if (choice.HasChoice || !expression.Alternatives.Any(branch => branch.SetEquals(choice.AllLicenses)))
            {
                throw new InvalidDataException("recorded selection is not a complete SPDX branch.");
            }
            selected = choice.AllLicenses;
        }
        else
        {
            selected = expression.AllLicenses;
            if (review?.SelectedLicense is not null && !Spdx.Parse(review.SelectedLicense).AllLicenses.SetEquals(selected))
            {
                throw new InvalidDataException("recorded selection drops an AND component.");
            }
        }
        selected.UnionWith(supplemental);
        foreach (var license in selected)
        {
            if (approvedDacFx && license == DacFxApproval.License)
            {
                continue;
            }
            if (allowed.Contains(license, StringComparer.Ordinal))
            {
                continue;
            }
            if (distributed || license is not ("EPL-2.0" or "MPL-2.0"))
            {
                throw new InvalidDataException($"license {license} is not allowed in {(distributed ? "distributed" : "tooling")} tier.");
            }
            if (exception is null || exception.License != license || string.IsNullOrWhiteSpace(exception.Purpose)
                || string.IsNullOrWhiteSpace(exception.Restriction) || string.IsNullOrWhiteSpace(exception.LicenseEvidence))
            {
                throw new InvalidDataException($"{license} requires an exact-version reviewed, unmodified, non-distributed tooling exception.");
            }
            // The existing exception is a narrow trust boundary, not an npm-wide allowance.
            if (package.Ecosystem == "npm" && package.Name == "elkjs"
                && (package.Version != "0.9.3" || package.Origin != ".github/scripts/package-lock.json"
                    || !exception.Purpose.Contains(".github/scripts only", StringComparison.Ordinal)
                    || !exception.Reviewers.SequenceEqual(new[] { "Cortado", "Cedar" })))
            {
                throw new InvalidDataException("elkjs exception is restricted to unmodified .github/scripts tooling, version 0.9.3.");
            }
            if (!package.Texts.Any(text => text.Path.EndsWith(exception.LicenseEvidence, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("tooling exception evidence is not among the collected license files.");
            }
        }
        var evidence = package.Texts.Select(text => text.Path).ToList();
        if (review is not null)
        {
            evidence.Add(review.EvidenceUrl!);
        }
        if (approvedDacFx)
        {
            evidence.AddRange([DacFxApproval.LicensePath, "sha256:" + DacFxApproval.LicenseHash, DacFxApproval.LicenseUrl, DacFxApproval.OwnerApproval]);
        }
        return new PackageDecision(package.Ecosystem, package.Name, package.Version, distributed ? "distributed" : "tooling",
            string.Join(" AND ", selected.Order(StringComparer.Ordinal)), evidence);
    }
}

internal sealed record LicenseFile(string Path, string Text, bool IsPrimary = true);
internal sealed record PackageEvidence(string Ecosystem, string Name, string Version, string Origin, string? Metadata,
    IReadOnlyList<LicenseFile> Texts, string? Archive = null)
{
    public string Key => Identity(Ecosystem, Name, Version);
    public static string Identity(string ecosystem, string name, string version) =>
        $"{ecosystem}:{(ecosystem == "npm" ? name : ecosystem == "python" ? Regex.Replace(name.ToLowerInvariant(), "[-_.]+", "-") : name.ToLowerInvariant())}@{version.ToLowerInvariant()}";
}

internal sealed record Reviews(string Ecosystem, string Package, string Version, string License, string[] Reviewers,
    string Review, string? EvidenceUrl, string? SelectedLicense, string? Purpose, string? Restriction, string? LicenseEvidence,
    string? Tier = null, string? OwnerApproval = null, string[]? ArtifactScopes = null, string? LicenseSha256 = null)
{
    public bool Matches(PackageEvidence package) => PackageEvidence.Identity(Ecosystem, Package, Version) == package.Key;

    public static Reviews[] Read(string path, bool required)
    {
        if (!required && !File.Exists(path))
        {
            return [];
        }
        var records = JsonSerializer.Deserialize<Reviews[]>(File.ReadAllText(path), JsonOptions.CaseInsensitive)
            ?? throw new InvalidDataException($"Invalid review records: {path}");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record is null || record.Ecosystem is not ("npm" or "nuget" or "python") || string.IsNullOrWhiteSpace(record.Package)
                || string.IsNullOrWhiteSpace(record.Version) || string.IsNullOrWhiteSpace(record.License)
                || record.Reviewers is not { Length: > 0 } || record.Reviewers.Any(string.IsNullOrWhiteSpace)
                || !Https(record.Review) || (!required && !Https(record.EvidenceUrl))
                || record.Tier is not (null or "tooling" or "shipped")
                || !keys.Add(PackageEvidence.Identity(record.Ecosystem, record.Package, record.Version)))
            {
                throw new InvalidDataException($"Invalid, unreviewed, or duplicate exact-version review: {path}");
            }
            if (record.Tier == "shipped")
            {
                if (!required)
                {
                    throw new InvalidDataException("Shipped approval belongs in license-exceptions.json, not a generic license override.");
                }
                DacFxApproval.ValidateRecord(record);
            }
        }
        return records;
    }

    private static bool Https(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https";
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
}
