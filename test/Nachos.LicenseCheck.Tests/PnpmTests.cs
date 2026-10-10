using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class PnpmTests
{
    private static readonly string[] ExpectedPackages = ["@pnpm/native", "baseline", "consumer", "foreign", "leaf", "peer", "pnpm"];
    [Fact]
    public void BothGraphsIncludingOptionalForeignPackages_AreCollected()
    {
        using var fixture = new PnpmFixture();
        var report = fixture.Audit.Check();
        report.Errors.ShouldBeEmpty();
        report.Packages.Select(package => package.Package).Order().ShouldBe(
            ExpectedPackages.Order());
    }

    [Fact]
    public async Task ActualCommands_InventoryFetchAndAuditUseBothDocuments()
    {
        using var fixture = new PnpmFixture();
        (await Command(fixture, "inventory-pnpm", "--lock", fixture.Lock, "--report", fixture.Audit.Full("inventory.json"))).Exit.ShouldBe(0);
        using var inventory = JsonDocument.Parse(File.ReadAllText(fixture.Audit.Full("inventory.json")));
        inventory.RootElement.GetProperty("packages").GetArrayLength().ShouldBe(6);
        (await Command(fixture, "fetch-pnpm", "--lock", fixture.Lock, "--npm-archives", fixture.Audit.Full("npm-archives"))).Exit.ShouldBe(0);
        var result = await Command(fixture, "--repo", fixture.Audit.Root, "--nuget-inventory", fixture.Audit.Full("nuget.json"),
            "--nuget-cache", fixture.Audit.Full("cache"), "--api-publish", fixture.Audit.Full("artifacts/api"),
            "--cli-publish", fixture.Audit.Full("artifacts/cli"), "--npm-archives", fixture.Audit.Full("npm-archives"),
            "--report", fixture.Audit.Full("report.json"));
        result.Exit.ShouldBe(0, result.Output);
    }

    [Theory]
    [InlineData("manager")]
    [InlineData("application")]
    [InlineData("third-document")]
    [InlineData("version")]
    [InlineData("duplicate")]
    [InlineData("tag")]
    [InlineData("alias")]
    [InlineData("root-version")]
    [InlineData("manager-version")]
    [InlineData("missing-context")]
    [InlineData("missing-optional")]
    [InlineData("missing-peer")]
    [InlineData("unsafe-name")]
    [InlineData("integrity")]
    [InlineData("tarball")]
    [InlineData("extra-importer")]
    [InlineData("snapshot-extra")]
    [InlineData("platform-shape")]
    [InlineData("peer-shape")]
    [InlineData("boolean-shape")]
    public async Task UnsupportedOrInconsistentLock_FailsBeforeFetching(string mutation)
    {
        using var fixture = new PnpmFixture();
        var text = File.ReadAllText(fixture.Lock).Replace("\r\n", "\n", StringComparison.Ordinal);
        var separator = text.IndexOf("\n---", StringComparison.Ordinal);
        text = mutation switch
        {
            "manager" => text[(separator + 1)..],
            "application" => text[..separator],
            "third-document" => text + "\n---\nlockfileVersion: '9.0'\n",
            "version" => text.Replace("'9.0'", "'8.0'", StringComparison.Ordinal),
            "duplicate" => text.Replace("lockfileVersion: '9.0'", "lockfileVersion: '9.0'\nlockfileVersion: '9.0'", StringComparison.Ordinal),
            "tag" => text.Replace("'9.0'", "!!str '9.0'", StringComparison.Ordinal),
            "alias" => text.Replace("'9.0'", "&v '9.0'", StringComparison.Ordinal),
            "root-version" => text.Replace("specifier: 2.0.0", "specifier: 3.0.0", StringComparison.Ordinal),
            "manager-version" => text.Replace("specifier: 12.8.2", "specifier: 12.8.1", StringComparison.Ordinal),
            "missing-context" => text.Replace("'consumer@1.0.0(peer@2.0.0)':", "'consumer@1.0.0(peer@1.0.0)':", StringComparison.Ordinal),
            "missing-optional" => text.Replace("foreign: 1.0.0", "foreign: 9.0.0", StringComparison.Ordinal),
            "missing-peer" => text.Replace("peer: 2.0.0", "peer: 9.0.0", StringComparison.Ordinal),
            "unsafe-name" => text.Replace("leaf@", "../leaf@", StringComparison.Ordinal),
            "integrity" => text.Replace("sha512-", "sha256-", StringComparison.Ordinal),
            "tarball" => text.Replace("resolution: {integrity:", "resolution: {tarball: 'https://evil.invalid/a.tgz', integrity:", StringComparison.Ordinal),
            "extra-importer" => text.Replace("importers:\n", "importers:\n  other: {}\n", StringComparison.Ordinal),
            "platform-shape" => text.Replace("os: [linux]", "os: linux", StringComparison.Ordinal),
            "peer-shape" => text.Replace("peerDependencies: {peer: ^2.0.0}", "peerDependencies: [peer]", StringComparison.Ordinal),
            "boolean-shape" => text.Replace("os: [linux]", "os: [linux]\n    hasBin: maybe", StringComparison.Ordinal),
            _ => text + "\n  'omitted@1.0.0': {}\n"
        };
        fixture.Audit.WriteText("web/pnpm-lock.yaml", text);
        var result = await Command(fixture, "fetch-pnpm", "--lock", fixture.Lock, "--npm-archives", fixture.Audit.Full("npm-archives"));
        result.Exit.ShouldBe(2, result.Output);
        result.Output.ShouldContain("pnpm");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("tampered")]
    public void ManagerArchiveIsRequiredEvenWhenApplicationIsComplete(string change)
    {
        using var fixture = new PnpmFixture();
        var path = fixture.Archives["pnpm"];
        if (change == "missing") File.Delete(path);
        else File.AppendAllText(path, "tamper");
        fixture.Audit.Check().Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("packageManager")]
    [InlineData("npm-lock")]
    [InlineData("missing-lock")]
    [InlineData("workspace-overrides")]
    public void DiscoveryReconcilesManagerAndCorrectLock(string change)
    {
        using var fixture = new PnpmFixture();
        switch (change)
        {
            case "packageManager":
                fixture.Audit.WriteText("web/package.json", File.ReadAllText(fixture.Audit.Full("web/package.json"))
                    .Replace("pnpm@12.8.2", "npm@11.0.0", StringComparison.Ordinal));
                break;
            case "npm-lock": fixture.Audit.WriteText("web/package-lock.json", "{}"); break;
            case "missing-lock": File.Delete(fixture.Lock); break;
            default: fixture.Audit.WriteText("web/pnpm-workspace.yaml", "overrides:\n  leaf: '2.0.0'\n"); break;
        }
        fixture.Audit.Check().Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("dependencies", "{\"leaf\":\"2.0.0\"}")]
    [InlineData("dependencies", "{\"omitted\":\"1.0.0\"}")]
    [InlineData("peerDependencies", "{\"peer\":\"^3.0.0\"}")]
    [InlineData("optionalDependencies", "{}")]
    public void ArchiveDeclarationsCannotDisagreeWithSnapshots(string field, string value)
    {
        using var fixture = new PnpmFixture();
        fixture.ReplaceConsumerMetadata(field, value);
        fixture.Audit.Check().Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("prohibited")]
    public void PnpmArchivesRetainActualLicensePolicy(string kind)
    {
        using var fixture = new PnpmFixture(supplemental: kind == "unknown" ? "Unrecognized supplemental terms." : AuditFixture.Gpl);
        fixture.Audit.Check().Errors.ShouldContain(error => error.Contains(kind == "unknown" ? "unrecognized" : "prohibited", StringComparison.Ordinal));
    }

    [Fact]
    public void PeerContextMustIncludeTheDeclaredResolvedPeer()
    {
        using var fixture = new PnpmFixture();
        fixture.Audit.WriteText("web/pnpm-lock.yaml", File.ReadAllText(fixture.Lock)
            .Replace("(peer@2.0.0)", "(leaf@1.0.0)", StringComparison.Ordinal));
        fixture.Audit.Check().Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryCrossDocumentOccurrenceMustAgreeBeforeDeduplication(bool conflict)
    {
        using var fixture = new PnpmFixture();
        var text = File.ReadAllText(fixture.Lock).Replace("\r\n", "\n", StringComparison.Ordinal);
        var digest = "sha512-" + Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(fixture.Archives[conflict ? "leaf" : "peer"])));
        var position = text.IndexOf("snapshots:", StringComparison.Ordinal);
        text = text.Insert(position, $"  'peer@2.0.0':\n    resolution: {{integrity: {digest}}}\n");
        position = text.IndexOf("snapshots:\n", StringComparison.Ordinal) + "snapshots:\n".Length;
        text = text.Insert(position, "  'peer@2.0.0': {}\n");
        fixture.Audit.WriteText("web/pnpm-lock.yaml", text);
        var errors = fixture.Audit.Check().Errors;
        if (conflict) errors.ShouldContain(error => error.Contains("Conflicting pnpm", StringComparison.Ordinal));
        else errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("~1.0.0", true)]
    [InlineData("^1.0.0", true)]
    [InlineData(">=1 <2", true)]
    [InlineData("1.x", true)]
    [InlineData("1.0 - 2.0.0", true)]
    [InlineData("0.5 || >=1.0.0 <2", true)]
    [InlineData("^0.1.0", false)]
    [InlineData("<1.0.0", false)]
    [InlineData("latest", false)]
    [InlineData("file:../leaf", false)]
    public void ResolvedDependencyMustSatisfyTheActualArchiveRange(string range, bool allowed)
    {
        using var fixture = new PnpmFixture();
        fixture.ReplaceConsumerMetadata("dependencies", JsonSerializer.Serialize(new { leaf = range }));
        fixture.Audit.Check().Errors.Count.ShouldBe(allowed ? 0 : 1);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("license")]
    [InlineData("extra-license")]
    [InlineData("nested-extra-license")]
    public void InstalledEvidenceCannotReplaceOrDisagreeWithVerifiedArchive(string change)
    {
        using var fixture = new PnpmFixture();
        fixture.Audit.Write("web/node_modules/.pnpm/peer@2.0.0/node_modules/peer/package.json",
            new { name = "peer", version = "2.0.0", license = change == "metadata" ? "ISC" : "MIT" });
        fixture.Audit.WriteText("web/node_modules/.pnpm/peer@2.0.0/node_modules/peer/LICENSE",
            change == "license" ? "Changed license" : AuditFixture.Mit);
        if (change == "extra-license") fixture.Audit.WriteText("web/node_modules/.pnpm/peer@2.0.0/node_modules/peer/NOTICE", "Additional terms.");
        if (change == "nested-extra-license") fixture.Audit.WriteText("web/node_modules/.pnpm/peer@2.0.0/node_modules/peer/dist/vendor/NOTICE", "Additional terms.");
        fixture.Audit.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void ExactInstalledEvidenceIsCheckedInAdditionToAllArchives()
    {
        using var fixture = new PnpmFixture();
        fixture.Audit.Write("web/node_modules/.pnpm/peer@2.0.0/node_modules/peer/package.json", new { name = "peer", version = "2.0.0", license = "MIT" });
        fixture.Audit.WriteText("web/node_modules/.pnpm/peer@2.0.0/node_modules/peer/LICENSE", AuditFixture.Mit);
        fixture.Audit.Check().Errors.ShouldBeEmpty();
        File.Delete(fixture.Archives["peer"]);
        fixture.Audit.Check().Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DeclaredInstalledDocumentIsComparedForBothLicenseMetadataForms(bool legacyObject, bool changed)
    {
        using var fixture = new PnpmFixture();
        var metadata = fixture.DeclarePeerLicense(legacyObject);
        const string location = "web/node_modules/.pnpm/peer@2.0.0/node_modules/peer/";
        fixture.Audit.Write(location + "package.json", metadata);
        fixture.Audit.WriteText(location + "LICENSE", AuditFixture.Mit);
        fixture.Audit.WriteText(location + "TERMS.txt", changed ? "Changed declared terms." : AuditFixture.Mit);
        var report = fixture.Audit.Check();
        if (changed) report.Errors.ShouldContain(error => error.Contains("Installed pnpm license differs", StringComparison.Ordinal));
        else report.Errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("peer", true)]
    [InlineData("unrelated", false)]
    public void RegistryArchiveMayUseItsExactPackageBasename_NotAnArbitraryRoot(string root, bool valid)
    {
        using var fixture = new PnpmFixture(peerArchiveRoot: root);
        var report = fixture.Audit.Check();
        if (!valid) report.Errors.ShouldNotBeEmpty();
        else
        {
            report.Errors.ShouldBeEmpty();
            report.Packages.Single(package => package.Package == "peer").Evidence
                .ShouldContain(path => path.EndsWith("!peer/LICENSE", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("node v1.0", true)]
    [InlineData("node v9.0", false)]
    public void PublishedTypesArchiveUsesItsMatchingMajorMinorDirectory(string archiveRoot, bool valid)
    {
        using var fixture = new AuditFixture();
        var source = NpmReviewTests.Archive(fixture, packageName: "@types/node");
        using var bytes = new MemoryStream();
        using (var input = File.OpenRead(source))
        using (var gzipIn = new GZipStream(input, CompressionMode.Decompress))
        using (var tarIn = new TarReader(gzipIn))
        using (var gzipOut = new GZipStream(bytes, CompressionMode.Compress, leaveOpen: true))
        using (var tarOut = new TarWriter(gzipOut, leaveOpen: true))
        {
            while (tarIn.GetNextEntry() is { } entry)
            {
                using var content = new MemoryStream();
                entry.DataStream!.CopyTo(content);
                content.Position = 0;
                tarOut.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, archiveRoot + entry.Name["package".Length..]) { DataStream = content });
            }
        }
        var digest = SHA512.HashData(bytes.ToArray());
        File.WriteAllBytes(fixture.Full("npm-archives/" + Convert.ToHexString(SHA256.HashData(digest)).ToLowerInvariant() + ".tgz"), bytes.ToArray());
        var lockPath = fixture.Full(".github/scripts/package-lock.json");
        var locked = JsonNode.Parse(File.ReadAllText(lockPath))!;
        locked["packages"]!["node_modules/@types/node"]!["integrity"] = "sha512-" + Convert.ToBase64String(digest);
        File.WriteAllText(lockPath, locked.ToJsonString());
        var report = fixture.Check();
        if (valid)
        {
            report.Errors.ShouldBeEmpty();
            report.Packages.Single(package => package.Package == "@types/node").Evidence
                .ShouldContain(path => path.EndsWith("!node v1.0/LICENSE", StringComparison.Ordinal));
        }
        else report.Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("package/empty.js", true)]
    [InlineData("package/NOTICE.txt", false)]
    public void EmptyRegularArchiveFilesAreNotLinks_EmptyLicenseEvidenceStillFails(string path, bool allowed)
    {
        using var fixture = new AuditFixture();
        NpmReviewTests.Archive(fixture, extraPath: path, extraText: "");
        var report = fixture.Check();
        if (allowed) report.Errors.ShouldBeEmpty();
        else report.Errors.ShouldContain(error => error.Contains("unrecognized supplemental", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ImplicitOptionalPeerRequiresActualArchivePeerMetadata(bool declared)
    {
        using var fixture = new PnpmFixture();
        fixture.ReplaceConsumerMetadata("peerDependencies", "{}");
        if (declared) fixture.ReplaceConsumerMetadata("peerDependenciesMeta", """{"peer":{"optional":true}}""");
        fixture.Audit.WriteText("web/pnpm-lock.yaml", File.ReadAllText(fixture.Lock)
            .Replace("peerDependencies: {peer: ^2.0.0}",
                "peerDependencies: {peer: '*'}\n    peerDependenciesMeta: {peer: {optional: true}}", StringComparison.Ordinal));
        var report = fixture.Audit.Check();
        if (declared) report.Errors.ShouldBeEmpty();
        else report.Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActualBsdDocumentSuffixDoesNotHideSupplementalTerms(bool complete)
    {
        using var fixture = new AuditFixture();
        NpmReviewTests.Archive(fixture, extraPath: "package/dist/vendor/LICENSE.BSD",
            extraText: complete ? ReviewRegressionTests.CompleteLicense("BSD-3-Clause") : "Unknown supplemental conditions.");
        var report = fixture.Check();
        if (complete)
        {
            report.Errors.ShouldBeEmpty();
            report.Packages.Single(package => package.Package == "foreign").SelectedLicense.ShouldBe("BSD-3-Clause AND MIT");
        }
        else report.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void LinkedInstalledMetadataIsNotRead()
    {
        using var fixture = new PnpmFixture();
        fixture.Audit.Write("outside/package.json", new { name = "peer", version = "2.0.0", license = "MIT" });
        var location = fixture.Audit.Full("web/node_modules/.pnpm/peer@2.0.0/node_modules/peer");
        Directory.CreateDirectory(location);
        File.CreateSymbolicLink(Path.Combine(location, "package.json"), fixture.Audit.Full("outside/package.json"));
        fixture.Audit.Check().Errors.ShouldContain(error => error.Contains("Linked evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void DependencyLinkCannotEscapeThePhysicalStore()
    {
        using var fixture = new PnpmFixture();
        fixture.Audit.Write("outside/package.json", new { name = "peer", version = "2.0.0", license = "MIT" });
        var container = fixture.Audit.Full("web/node_modules/.pnpm/peer@2.0.0/node_modules");
        Directory.CreateDirectory(container);
        Directory.CreateSymbolicLink(Path.Combine(container, "peer"), fixture.Audit.Full("outside"));
        fixture.Audit.Check().Errors.ShouldContain(error => error.Contains("escapes evidence root", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ContainedDependencyLinkMustResolveToExistingPhysicalEvidence(bool exists)
    {
        using var fixture = new PnpmFixture();
        var peer = "web/node_modules/.pnpm/peer@2.0.0/node_modules/peer";
        if (exists)
        {
            fixture.Audit.Write(peer + "/package.json", new { name = "peer", version = "2.0.0", license = "MIT" });
            fixture.Audit.WriteText(peer + "/LICENSE", AuditFixture.Mit);
        }
        var consumer = fixture.Audit.Full("web/node_modules/.pnpm/consumer@1.0.0_peer@2.0.0/node_modules");
        Directory.CreateDirectory(consumer);
        Directory.CreateSymbolicLink(Path.Combine(consumer, "peer"), fixture.Audit.Full(peer));
        var report = fixture.Audit.Check();
        if (exists) report.Errors.ShouldBeEmpty();
        else report.Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactParentRemovalIsAppliedOnlyToThatParent(bool matchingParent)
    {
        using var fixture = new PnpmFixture();
        var rule = matchingParent ? "consumer@1.0.0>foreign" : "consumer@2.0.0>foreign";
        fixture.Audit.WriteText("web/pnpm-workspace.yaml", $"overrides:\n  '{rule}': '-'\n");
        fixture.Audit.WriteText("web/pnpm-lock.yaml", File.ReadAllText(fixture.Lock)
            .Replace("overrides: {}", $"overrides:\n  '{rule}': '-'", StringComparison.Ordinal)
            .Replace("optionalDependencies: {foreign: 1.0.0}", "optionalDependencies: {}", StringComparison.Ordinal));
        var report = fixture.Audit.Check();
        if (matchingParent) report.Errors.ShouldBeEmpty();
        else report.Errors.ShouldNotBeEmpty();
        // A removed edge does not hide an independently recorded package.
        report.Packages.ShouldContain(package => package.Package == "foreign");
    }

    internal static async Task<(int Exit, string Output)> Command(PnpmFixture fixture, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(typeof(LicenseAudit).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout + await stderr);
    }
}

internal sealed class PnpmFixture : IDisposable
{
    private static readonly string[] Linux = ["linux"];
    public AuditFixture Audit { get; } = new();
    public Dictionary<string, string> Archives { get; } = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> integrities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> consumer = new(StringComparer.Ordinal)
    {
        ["name"] = "consumer", ["version"] = "1.0.0", ["license"] = "MIT",
        ["dependencies"] = new { leaf = "~1.0.0" }, ["optionalDependencies"] = new { foreign = "1.0.0" },
        ["peerDependencies"] = new { peer = "^2.0.0" }
    };
    public string Lock => Audit.Full("web/pnpm-lock.yaml");

    public PnpmFixture(string? supplemental = null, string peerArchiveRoot = "package")
    {
        Archive("pnpm", "12.8.2", new { name = "pnpm", version = "12.8.2", license = "MIT",
            optionalDependencies = new Dictionary<string, string> { ["@pnpm/native"] = "12.8.2" } });
        Archive("@pnpm/native", "12.8.2", new { name = "@pnpm/native", version = "12.8.2", license = "MIT", os = Linux });
        Archive("consumer", "1.0.0", consumer, supplemental);
        Archive("peer", "2.0.0", new { name = "peer", version = "2.0.0", license = "MIT" }, root: peerArchiveRoot);
        Archive("leaf", "1.0.0", new { name = "leaf", version = "1.0.0", license = "MIT" });
        Archive("foreign", "1.0.0", new { name = "foreign", version = "1.0.0", license = "MIT", os = Linux });
        Audit.Write("web/package.json", new { name = "fixture", packageManager = "pnpm@12.8.2",
            dependencies = new { consumer = "1.0.0", peer = "2.0.0" } });
        Audit.WriteText("web/pnpm-workspace.yaml", "minimumReleaseAge: 1440\nminimumReleaseAgeStrict: true\noverrides: {}\n");
        WriteLock();
    }

    public void ReplaceConsumerMetadata(string field, string value)
    {
        consumer[field] = JsonSerializer.Deserialize<JsonElement>(value);
        Archive("consumer", "1.0.0", consumer);
        WriteLock();
    }

    public object DeclarePeerLicense(bool legacyObject)
    {
        object license = legacyObject ? new { type = "SEE LICENSE IN TERMS.txt" } : "SEE LICENSE IN TERMS.txt";
        var metadata = new { name = "peer", version = "2.0.0", license };
        Archive("peer", "2.0.0", metadata, declared: true);
        WriteLock();
        return metadata;
    }

    private void Archive(string name, string version, object metadata, string? supplemental = null, string root = "package", bool declared = false)
    {
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionMode.Compress, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            void Add(string path, string text)
            {
                using var data = new MemoryStream(Encoding.UTF8.GetBytes(text));
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, root + "/" + path) { DataStream = data });
            }
            Add("package.json", JsonSerializer.Serialize(metadata));
            Add("LICENSE", AuditFixture.Mit);
            if (declared) Add("TERMS.txt", AuditFixture.Mit);
            if (supplemental is not null) Add("THIRD-PARTY-NOTICES.txt", supplemental);
        }
        var digest = SHA512.HashData(bytes.ToArray());
        var path = Audit.Full("npm-archives/" + Convert.ToHexString(SHA256.HashData(digest)).ToLowerInvariant() + ".tgz");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes.ToArray());
        Archives[name] = path;
        integrities[name] = "sha512-" + Convert.ToBase64String(digest);
    }

    private void WriteLock() => Audit.WriteText("web/pnpm-lock.yaml", $$"""
        ---
        lockfileVersion: '9.0'
        importers:
          .:
            configDependencies: {}
            packageManagerDependencies:
              pnpm: {specifier: 12.8.2, version: 12.8.2}
        packages:
          'pnpm@12.8.2':
            resolution: {integrity: {{integrities["pnpm"]}}}
          '@pnpm/native@12.8.2':
            resolution: {integrity: {{integrities["@pnpm/native"]}}}
            os: [linux]
        snapshots:
          'pnpm@12.8.2':
            optionalDependencies: {'@pnpm/native': 12.8.2}
          '@pnpm/native@12.8.2':
            optional: true
        ---
        lockfileVersion: '9.0'
        settings: {autoInstallPeers: true, excludeLinksFromLockfile: false}
        overrides: {}
        importers:
          .:
            dependencies:
              consumer: {specifier: 1.0.0, version: '1.0.0(peer@2.0.0)'}
              peer: {specifier: 2.0.0, version: 2.0.0}
        packages:
          'consumer@1.0.0':
            resolution: {integrity: {{integrities["consumer"]}}}
            peerDependencies: {peer: ^2.0.0}
          'peer@2.0.0':
            resolution: {integrity: {{integrities["peer"]}}}
          'leaf@1.0.0':
            resolution: {integrity: {{integrities["leaf"]}}}
          'foreign@1.0.0':
            resolution: {integrity: {{integrities["foreign"]}}}
            os: [linux]
        snapshots:
          'consumer@1.0.0(peer@2.0.0)':
            dependencies: {leaf: 1.0.0, peer: 2.0.0}
            optionalDependencies: {foreign: 1.0.0}
          'peer@2.0.0': {}
          'leaf@1.0.0': {}
          'foreign@1.0.0':
            optional: true
        """);

    public void Dispose() => Audit.Dispose();
}
