using System.Formats.Tar;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class NpmReviewTests
{
    [Fact]
    public void CI4_EmptyLockInventory_CannotSatisfyManifest()
    {
        using var fixture = new AuditFixture();
        fixture.Write(".github/scripts/package.json", new { name = "fixture", version = "1.0.0", dependencies = new { baseline = "1.0.0" } });
        fixture.Write(".github/scripts/package-lock.json", new { lockfileVersion = 3, packages = new { } });
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void CI4_RootLockMustAgreeWithManifest()
    {
        using var fixture = new AuditFixture();
        fixture.Write(".github/scripts/package.json", new { name = "fixture", version = "1.0.0", dependencies = new { baseline = "1.0.0" } });
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("dependencies")]
    [InlineData("peerDependencies")]
    public void CI4_MissingRequiredDependencyRecord_Fails(string kind)
    {
        using var fixture = new AuditFixture();
        SetDeclaration(fixture, "node_modules/baseline", kind, new JsonObject { ["missing"] = "^1.0.0" });
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void CI4_NestedDependency_ResolvesAtNearestPackage()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("baseline/node_modules/nested", "MIT", AuditFixture.Mit);
        SetDeclaration(fixture, "node_modules/baseline", "dependencies", new JsonObject { ["nested"] = "1.0.0" });
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Fact]
    public void CI4_AbsentUnresolvedOptionalDependencyAndPeer_ArePermitted()
    {
        using var fixture = new AuditFixture();
        SetDeclaration(fixture, "node_modules/baseline", "optionalDependencies", new JsonObject { ["optional"] = "^1.0.0" });
        SetDeclaration(fixture, "node_modules/baseline", "peerDependencies", new JsonObject { ["peer"] = "^1.0.0" });
        SetDeclaration(fixture, "node_modules/baseline", "peerDependenciesMeta", new JsonObject { ["peer"] = new JsonObject { ["optional"] = true } });
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Fact]
    public void CI7_ValidLockedForeignPlatformArchive_IsAuditedWithoutInstallation()
    {
        using var fixture = new AuditFixture();
        Archive(fixture);
        var report = fixture.Check();
        report.Errors.ShouldBeEmpty();
        report.Packages.ShouldContain(package => package.Package == "foreign" && package.Version == "1.0.0");
        Directory.Exists(fixture.Full(".github/scripts/node_modules/foreign")).ShouldBeFalse();
    }

    [Fact]
    public void CI7_TamperedArchive_FailsIntegrity()
    {
        using var fixture = new AuditFixture();
        var archive = Archive(fixture);
        File.AppendAllText(archive, "tampered");
        fixture.Check().Errors.ShouldContain(error => error.Contains("integrity", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("other", "1.0.0", "MIT")]
    [InlineData("foreign", "2.0.0", "MIT")]
    [InlineData("foreign", "1.0.0", "ISC")]
    public void CI7_ArchiveMetadataMustMatchLock(string name, string version, string license)
    {
        using var fixture = new AuditFixture();
        Archive(fixture, metadata: new { name, version, license });
        fixture.Check().Errors.ShouldContain(error => error.Contains("disagrees", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("package/../escape.txt")]
    [InlineData("/package/LICENSE")]
    [InlineData("package\\LICENSE")]
    [InlineData("package/C:/LICENSE")]
    public void CI7_UnsafeArchivePaths_Fail(string path)
    {
        using var fixture = new AuditFixture();
        Archive(fixture, extraPath: path);
        fixture.Check().Errors.ShouldContain(error => error.Contains("Unsafe", StringComparison.Ordinal));
    }

    [Fact]
    public void CI7_OversizedEvidence_FailsBeforeUnboundedRead()
    {
        using var fixture = new AuditFixture();
        Archive(fixture, extraPath: "package/NOTICE.txt", extraText: new string('X', 2 * 1024 * 1024 + 1));
        fixture.Check().Errors.ShouldContain(error => error.Contains("limit", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://example.test/foreign.tgz")]
    [InlineData("https://registry.npmjs.org/other/-/other-1.0.0.tgz")]
    [InlineData("http://registry.npmjs.org/foreign/-/foreign-1.0.0.tgz")]
    public void CI7_UnexpectedRegistryProvenance_FailsWithoutNetwork(string resolved)
    {
        using var fixture = new AuditFixture();
        Archive(fixture, resolved: resolved);
        fixture.Check().Errors.ShouldContain(error => error.Contains("registry provenance", StringComparison.Ordinal));
    }

    [Fact]
    public void CI7_MissingArchive_FailsClosed()
    {
        using var fixture = new AuditFixture();
        File.Delete(Archive(fixture));
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void CI8_ImplicitUpdateScript_DoesNotInvalidateRealLicense()
    {
        using var fixture = new AuditFixture();
        fixture.WriteText(".github/scripts/node_modules/baseline/license-update.mjs", "DO NOT READ OR EXECUTE SDK IMPLEMENTATION");
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Fact]
    public void CI8_UpdateScript_IsNotEvidenceWhenLicenseIsMissing()
    {
        using var fixture = new AuditFixture();
        File.Delete(fixture.Full(".github/scripts/node_modules/baseline/LICENSE"));
        fixture.WriteText(".github/scripts/node_modules/baseline/license-update.mjs", AuditFixture.Mit);
        fixture.Check().Errors.ShouldContain(error => error.Contains("text unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void CI8_ExplicitSourceEvidence_FailsWithoutReadingIt()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("source", "SEE LICENSE IN license-update.mjs", null);
        fixture.WriteText(".github/scripts/node_modules/source/license-update.mjs", "DO NOT READ SDK SOURCE");
        fixture.Check().Errors.ShouldContain(error => error.Contains("unsupported license entry", StringComparison.Ordinal));
    }

    [Fact]
    public void CI4_InstalledDependenciesCannotDisappearFromLock()
    {
        using var fixture = new AuditFixture();
        fixture.Write(".github/scripts/node_modules/baseline/package.json",
            new { name = "baseline", version = "1.0.0", license = "MIT", dependencies = new { omitted = "^1.0.0" } });
        fixture.Check().Errors.ShouldContain(error => error.Contains("dependencies disagree", StringComparison.Ordinal));
    }

    [Fact]
    public void CI4_TransitiveDependencyCanResolveHoisted()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("hoisted", "MIT", AuditFixture.Mit);
        SetDeclaration(fixture, "node_modules/baseline", "dependencies", new JsonObject { ["hoisted"] = "^1.0.0" });
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sha1-Zml4dHVyZQ==")]
    [InlineData("sha512-invalid")]
    [InlineData("sha512-Zml4dHVyZQ==")]
    public void CI7_MissingOrMalformedIntegrity_Fails(string? integrity)
    {
        using var fixture = new AuditFixture();
        Archive(fixture);
        ChangeLock(fixture, record => record["integrity"] = integrity);
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void CI7_DuplicateArchivePaths_Fail()
    {
        using var fixture = new AuditFixture();
        Archive(fixture, extraPath: "package/LICENSE");
        fixture.Check().Errors.ShouldContain(error => error.Contains("duplicate", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    public void CI7_LinkedArchiveEntries_Fail(TarEntryType kind)
    {
        using var fixture = new AuditFixture();
        Archive(fixture, extraPath: "package/link", extraKind: kind);
        fixture.Check().Errors.ShouldContain(error => error.Contains("linked/special", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SEE LICENSE IN terms.txt", "terms.txt", true)]
    [InlineData("SEE LICENSE IN missing.txt", null, false)]
    [InlineData("SEE LICENSE IN license-update.mjs", null, false)]
    public void CI7_DeclaredArchiveEvidence_IsRequiredAndValidated(string license, string? path, bool pass)
    {
        using var fixture = new AuditFixture();
        Archive(fixture, metadata: new { name = "foreign", version = "1.0.0", license },
            extraPath: path is null ? null : "package/" + path, extraText: AuditFixture.Mit);
        ChangeLock(fixture, record => record["license"] = license);
        var report = fixture.Check();
        if (pass)
        {
            report.Errors.ShouldBeEmpty();
        }
        else
        {
            report.Errors.ShouldNotBeEmpty();
        }
    }

    [Fact]
    public void CI7_ExistingBrokenInstallation_DoesNotUseArchiveFallback()
    {
        using var fixture = new AuditFixture();
        Archive(fixture);
        Directory.CreateDirectory(fixture.Full(".github/scripts/node_modules/foreign"));
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void CI7_CorruptGzipWithMatchingIntegrity_Fails()
    {
        using var fixture = new AuditFixture();
        var path = Archive(fixture);
        var bytes = Encoding.UTF8.GetBytes("not a gzip archive");
        var hash = SHA512.HashData(bytes);
        File.Delete(path);
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!, Convert.ToHexString(SHA256.HashData(hash)).ToLowerInvariant() + ".tgz"), bytes);
        ChangeLock(fixture, record => record["integrity"] = "sha512-" + Convert.ToBase64String(hash));
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void CI7_FetchCommand_ValidatesCachedEvidenceOffline()
    {
        using var fixture = new AuditFixture();
        var path = Archive(fixture);
        Fetch(fixture).ShouldBe(0);
        File.AppendAllText(path, "tampered");
        Fetch(fixture).ShouldBe(2);
    }

    [Fact]
    public void CI7_FetchCommand_RejectsUntrustedProvenanceBeforeNetwork()
    {
        using var fixture = new AuditFixture();
        Archive(fixture, resolved: "https://example.test/foreign.tgz");
        Fetch(fixture).ShouldBe(2);
    }

    [Fact]
    public void CI7_ScopedForeignPlatformArchive_IsAudited()
    {
        using var fixture = new AuditFixture();
        Archive(fixture, packageName: "@fixture/foreign");
        var report = fixture.Check();
        report.Errors.ShouldBeEmpty();
        report.Packages.ShouldContain(package => package.Package == "@fixture/foreign");
    }

    private static int Fetch(AuditFixture fixture)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { typeof(LicenseAudit).Assembly.Location, "fetch-npm", "--lock",
            fixture.Full(".github/scripts/package-lock.json"), "--npm-archives", fixture.Full("npm-archives") })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Offline fetch fixture unexpectedly timed out.");
        }
        Task.WaitAll(stdout, stderr);
        return process.ExitCode;
    }

    private static void ChangeLock(AuditFixture fixture, Action<JsonNode> change)
    {
        var path = fixture.Full(".github/scripts/package-lock.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        change(document["packages"]!["node_modules/foreign"]!);
        File.WriteAllText(path, document.ToJsonString());
    }

    internal static void SetDeclaration(AuditFixture fixture, string packagePath, string kind, JsonObject value)
    {
        var lockPath = fixture.Full(".github/scripts/package-lock.json");
        var document = JsonNode.Parse(File.ReadAllText(lockPath))!;
        document["packages"]![packagePath]![kind] = value.DeepClone();
        File.WriteAllText(lockPath, document.ToJsonString());
        var metadataPath = fixture.Full(".github/scripts/" + packagePath + "/package.json");
        var metadata = JsonNode.Parse(File.ReadAllText(metadataPath))!;
        metadata[kind] = value.DeepClone();
        File.WriteAllText(metadataPath, metadata.ToJsonString());
    }

    internal static string Archive(AuditFixture fixture, object? metadata = null, string? extraPath = null,
        string extraText = "fixture content", string? resolved = null, TarEntryType extraKind = TarEntryType.RegularFile,
        string packageName = "foreign")
    {
        fixture.Npm(packageName, "MIT", AuditFixture.Mit);
        Directory.Delete(fixture.Full(".github/scripts/node_modules/" + packageName), true);
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionMode.Compress, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            void Add(string path, string text)
            {
                using var content = new MemoryStream(Encoding.UTF8.GetBytes(text));
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = content });
            }
            Add("package/package.json", JsonSerializer.Serialize(metadata ?? new { name = packageName, version = "1.0.0", license = "MIT" }));
            Add("package/LICENSE", AuditFixture.Mit);
            Add("package/license-update.mjs", "DO NOT READ OR EXECUTE IMPLEMENTATION");
            if (extraPath is not null)
            {
                if (extraKind == TarEntryType.RegularFile)
                {
                    Add(extraPath, extraText);
                }
                else
                {
                    tar.WriteEntry(new PaxTarEntry(extraKind, extraPath) { LinkName = "package/LICENSE" });
                }
            }
        }
        var digest = SHA512.HashData(bytes.ToArray());
        var directory = fixture.Full("npm-archives");
        Directory.CreateDirectory(directory);
        var archive = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(digest)).ToLowerInvariant() + ".tgz");
        File.WriteAllBytes(archive, bytes.ToArray());
        var lockPath = fixture.Full(".github/scripts/package-lock.json");
        var document = JsonNode.Parse(File.ReadAllText(lockPath))!;
        var record = document["packages"]!["node_modules/" + packageName]!;
        record["optional"] = true;
        record["os"] = new JsonArray("nonexistent-fixture-platform");
        record["resolved"] = resolved ?? $"https://registry.npmjs.org/{packageName}/-/{packageName.Split('/')[^1]}-1.0.0.tgz";
        record["integrity"] = "sha512-" + Convert.ToBase64String(digest);
        File.WriteAllText(lockPath, document.ToJsonString());
        return archive;
    }
}
