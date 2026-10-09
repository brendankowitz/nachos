using System.IO.Compression;
using System.Security.Cryptography;

namespace Nachos.LicenseCheck;

internal static class MicrosoftPrimaryApproval
{
    public const string Type = "microsoft-primary";
    private const string OwnerApproval = "https://github.com/brendankowitz/nachos/pull/6#issuecomment-6083936795";
    private const string OwnerPath = "eng/licenses/microsoft-library-policy/owner-ms-license-6083936795.json";
    private const string OwnerHash = "7a843523fecd8769a984ed13ca94f6daa891e90c5dda873da504bb9694736753";

    // These are reviewed artifacts, not a publisher-name heuristic or a grant of new rights.
    private static readonly Primary[] Reviewed =
    [
        new("Microsoft.Data.SqlClient.SNI.runtime", "6.0.2", "LICENSE.txt",
            "LicenseRef-Microsoft-SqlClient-SNI-6.0.2",
            "9335e8bad875dd7be4eebd55d2335eb6433d1cea61aadb3817af7807bef8932a",
            "66232cc42f75a62d47f6662b6c1235b322bdd39ea5908a8a31a0fd7fd21f31c9",
            "090b897e3658a11c5f734edc4b350d08e39dce6509bb4a75d09f96a5c1b6049d",
            "distributable-code-conditions"),
        new("Microsoft.Data.SqlClient.SNI.runtime", "6.0.3", "LICENSE.txt",
            "LicenseRef-Microsoft-SqlClient-SNI-6.0.3",
            "9335e8bad875dd7be4eebd55d2335eb6433d1cea61aadb3817af7807bef8932a",
            "d5384233109efc8ca42e51d7e1f7f3d35d47eb5a878843e3002c77550babcd82",
            "b9df07c20101398f77cf16b209afefafcc7190d6ac0b6e244e81a2fed4c96f5f",
            "distributable-code-conditions"),
        new("Microsoft.SqlServer.Types", "170.1000.7", "license.md",
            "LicenseRef-Microsoft-SQL-Server-Types-170.1000.7",
            "e4b4088d14de78a57d485d0bc53f3250f3e1d2376993ddb2c5b165eca3d59d40",
            "cfdc24005bcba7ba5aff56c8fe146e10e5f9d39b526e248b56cd8f703bb6ba99",
            "cf5a138692bd7683a971d030013eda563cd54ce472f52ffd62e96350ff1f9970",
            "unresolved-pre-release-2022")
    ];

    public static void ValidateRecord(Reviews record)
    {
        var primary = Find(record.Ecosystem, record.Package, record.Version);
        if (record.ApprovalType != Type || record.Tier != "shipped" || record.License != primary.License
            || record.SelectedLicense is not null
            || record.OwnerApproval != OwnerApproval || record.Review != OwnerApproval
            || !record.Reviewers.SequenceEqual(["brendankowitz"])
            || record.OwnerEvidence != OwnerPath || !SameHash(record.OwnerEvidenceSha256, OwnerHash)
            || record.EvidenceUrl != primary.LicenseUrl
            || record.LicenseEntry != primary.Entry || record.LicenseEvidence != primary.LicensePath
            || !SameHash(record.LicenseSha256, primary.LicenseHash)
            || record.OriginEvidence != primary.OriginPath || !SameHash(record.OriginSha256, primary.OriginHash)
            || !SameHash(record.ArchiveSha256, primary.ArchiveHash)
            || record.GoverningTerms != primary.GoverningTerms
            || record.ArtifactScopes is not { Length: 2 }
            || !record.ArtifactScopes.ToHashSet(StringComparer.Ordinal).SetEquals(["api", "cli"])
            || string.IsNullOrWhiteSpace(record.Purpose) || string.IsNullOrWhiteSpace(record.Restriction))
        {
            throw new InvalidDataException("Microsoft primary approval does not match the reviewed identity, actual owner decision, origin, exact text, governing-terms qualification and API/CLI scope.");
        }
    }

    public static (int Index, string License, string[] Evidence) Authorize(AuditInputs inputs,
        PackageEvidence package, HashSet<string> scopes)
    {
        var primary = Find(package.Ecosystem, package.Name, package.Version);
        if (scopes.Count == 0 || scopes.Any(scope => scope is not ("api" or "cli")))
        {
            throw new InvalidDataException("Microsoft primary shipped approval requires proved API/CLI distribution, not tooling or notice copies alone.");
        }
        RequireHash(Collectors.Under(inputs.Root, OwnerPath), OwnerHash);
        var source = Collectors.Under(inputs.Root, primary.LicensePath);
        RequireHash(source, primary.LicenseHash);
        RequireHash(Collectors.Under(inputs.Root, primary.OriginPath), primary.OriginHash);
        var candidates = package.Texts.Select((file, index) => (file, index)).Where(item => item.file.IsPrimary).ToArray();
        if (package.Archive is null || candidates.Length != 1
            || candidates[0].file.Path != package.Archive + "!" + primary.Entry
            || candidates[0].file.Text != File.ReadAllText(source))
        {
            throw new InvalidDataException("Microsoft primary approval requires exactly the complete reviewed primary entry; missing, additional or changed primary text is not authorized.");
        }
        // Pin the archive on the same stream used for documentary reads; no payload is executed.
        using var stream = File.OpenRead(package.Archive);
        if (!SameHash(Convert.ToHexString(SHA256.HashData(stream)), primary.ArchiveHash))
        {
            throw new InvalidDataException("Microsoft primary archive SHA256 differs from the reviewed package.");
        }
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        RequireEntry(archive, primary.Entry, primary.LicenseHash);
        RequireEntry(archive, primary.Package + ".nuspec", primary.OriginHash);
        foreach (var scope in scopes)
        {
            var publish = scope == "api" ? inputs.ApiPublish : inputs.CliPublish;
            RequireHash(Collectors.Under(publish, primary.LicensePath), primary.LicenseHash);
        }
        return (candidates[0].index, primary.License,
            [primary.LicensePath, "sha256:" + primary.LicenseHash,
                primary.OriginPath, "origin-sha256:" + primary.OriginHash,
                "archive-sha256:" + primary.ArchiveHash, primary.LicenseUrl,
                OwnerPath, "owner-sha256:" + OwnerHash, OwnerApproval,
                "governing-terms:" + primary.GoverningTerms]);
    }

    public static bool IsSourceNotice(string root, string relative, string publishedHash)
    {
        var primary = Reviewed.SingleOrDefault(entry => entry.LicensePath == relative);
        if (primary is null) { return false; }
        RequireHash(Collectors.Under(root, relative), primary.LicenseHash);
        if (!SameHash(publishedHash, primary.LicenseHash))
        {
            throw new InvalidDataException($"Microsoft primary required source notice content differs in artifact: {relative}");
        }
        return true;
    }

    private static Primary Find(string ecosystem, string package, string version) =>
        Reviewed.SingleOrDefault(entry => ecosystem == "nuget"
            && entry.Package.Equals(package, StringComparison.OrdinalIgnoreCase) && entry.Version == version)
        ?? throw new InvalidDataException("No reviewed Microsoft primary origin and exact package/version identity; names and authors do not confer approval.");

    private static bool SameHash(string? actual, string expected) =>
        expected.Equals(actual, StringComparison.OrdinalIgnoreCase);

    private static void RequireHash(string path, string expected)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Required Microsoft primary evidence or notice is missing: {path}");
        }
        using var stream = File.OpenRead(path);
        if (!SameHash(Convert.ToHexString(SHA256.HashData(stream)), expected))
        {
            throw new InvalidDataException($"Microsoft primary evidence or notice differs from the reviewed SHA256: {path}");
        }
    }

    private static void RequireEntry(ZipArchive archive, string name, string expected)
    {
        var entries = archive.Entries.Where(entry => entry.FullName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length != 1 || entries[0].FullName != name)
        {
            throw new InvalidDataException($"Microsoft primary archive entry is missing, ambiguous or duplicated: {name}");
        }
        using var stream = entries[0].Open();
        if (!SameHash(Convert.ToHexString(SHA256.HashData(stream)), expected))
        {
            throw new InvalidDataException($"Microsoft primary archive entry differs from the reviewed SHA256: {name}");
        }
    }

    private sealed record Primary(string Package, string Version, string Entry, string License,
        string LicenseHash, string OriginHash, string ArchiveHash, string GoverningTerms)
    {
        public string LicensePath => $"eng/licenses/{Package}/{Version}/{Entry}";
        public string OriginPath => $"eng/licenses/{Package}/{Version}/{Package}.nuspec";
        public string LicenseUrl => $"https://www.nuget.org/packages/{Package}/{Version}/License";
    }
}
