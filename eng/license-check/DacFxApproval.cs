using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Nachos.LicenseCheck;

internal static class DacFxApproval
{
    public const string Package = "Microsoft.SqlServer.DacFx";
    public const string Version = "170.4.83";
    public const string License = "LicenseRef-Microsoft-SQL-Server-DacFx-170.4.83";
    public const string OwnerApproval = "https://github.com/brendankowitz/nachos/issues/2#issuecomment-6048060868";
    public const string LicenseUrl = "https://www.nuget.org/packages/Microsoft.SqlServer.DacFx/170.4.83/License";
    public const string LicensePath = "eng/licenses/Microsoft.SqlServer.DacFx/170.4.83/license.txt";
    public const string LicenseHash = "f6b3be3e53b8b6836b9c08fee9e9fb24e698df2ab2a7e4afd1f77ebf10c22a83";
    private const string NoticePath = "THIRD-PARTY-NOTICES.md";
    private const string NoticeHash = "d727a48b88eba908f2f52b0809277e3f55d27bb322641fb8dfb779b6b9034a04";

    public static void ValidateRecord(Reviews record)
    {
        if (record.Ecosystem != "nuget" || !record.Package.Equals(Package, StringComparison.OrdinalIgnoreCase)
            || record.Version != Version || record.License != License || record.Tier != "shipped" || record.SelectedLicense is not null
            || record.Review != OwnerApproval || record.OwnerApproval != OwnerApproval
            || !record.Reviewers.SequenceEqual(["repository-owner"])
            || record.EvidenceUrl != LicenseUrl || record.LicenseEvidence != LicensePath
            || !LicenseHash.Equals(record.LicenseSha256, StringComparison.OrdinalIgnoreCase)
            || record.ArtifactScopes is not { Length: 2 }
            || !record.ArtifactScopes.ToHashSet(StringComparer.Ordinal).SetEquals(["api", "cli"])
            || string.IsNullOrWhiteSpace(record.Purpose) || string.IsNullOrWhiteSpace(record.Restriction))
        {
            throw new InvalidDataException("Shipped exception does not match the exact owner-approved DacFx identity, evidence, tier and API/CLI scope.");
        }
    }

    public static int Authorize(AuditInputs inputs, PackageEvidence package, HashSet<string> scopes)
    {
        if (scopes.Count == 0 || scopes.Any(scope => scope is not ("api" or "cli")))
        {
            throw new InvalidDataException("DacFx shipped approval requires proved API/CLI distribution; it is not a general tooling permission.");
        }
        var source = Collectors.Under(inputs.Root, LicensePath);
        SourceNoticeHash(inputs.Root, LicensePath);
        var candidates = package.Texts.Select((file, index) => (file, index))
            .Where(item => item.file.IsPrimary && item.file.Path == package.Archive + "!license.txt").ToArray();
        if (package.Archive is null || candidates.Length != 1
            || candidates[0].file.Text != File.ReadAllText(source))
        {
            throw new InvalidDataException("DacFx requires the unchanged reviewed primary license.txt from its exact NuGet package.");
        }
        using var archive = ZipFile.OpenRead(package.Archive);
        var entries = archive.Entries.Where(entry => entry.FullName == "license.txt").ToArray();
        if (entries.Length != 1)
        {
            throw new InvalidDataException("DacFx primary license entry is missing or duplicated.");
        }
        using var text = entries[0].Open();
        if (!Convert.ToHexString(SHA256.HashData(text)).Equals(LicenseHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("DacFx primary license bytes differ from the reviewed SHA256.");
        }
        foreach (var scope in scopes)
        {
            var publish = scope == "api" ? inputs.ApiPublish : inputs.CliPublish;
            foreach (var relative in new[] { NoticePath, LicensePath })
            {
                if (HashRequired(publish, relative) != SourceNoticeHash(inputs.Root, relative))
                {
                    throw new InvalidDataException($"DacFx required notice copy differs from source: {scope}/{relative}");
                }
            }
        }
        return candidates[0].index;
    }

    public static bool IsSourceNotice(string root, string relative, string publishedHash)
    {
        if (relative is not (NoticePath or LicensePath))
        {
            return false;
        }
        if (!publishedHash.Equals(SourceNoticeHash(root, relative), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Required source notice content differs in artifact: {relative}");
        }
        return true;
    }

    private static string SourceNoticeHash(string root, string relative)
    {
        var rawHash = HashRequired(root, relative);
        if (relative == LicensePath)
        {
            if (rawHash != LicenseHash)
            {
                throw new InvalidDataException("DacFx source license evidence differs from the reviewed SHA256.");
            }
        }
        else
        {
            // Git may change Markdown line endings. License bytes are never normalized.
            var notice = File.ReadAllText(Collectors.Under(root, relative)).Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(notice))).Equals(NoticeHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Required DacFx source notice content has changed; its copy contract needs review.");
            }
        }
        return rawHash;
    }

    private static string HashRequired(string root, string relative)
    {
        var path = Collectors.Under(root, relative);
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Required DacFx evidence or notice is missing: {path}");
        }
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
