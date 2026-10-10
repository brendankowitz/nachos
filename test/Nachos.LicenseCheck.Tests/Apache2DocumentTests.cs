using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.LicenseCheck;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class Apache2DocumentTests
{
    [Theory]
    [InlineData("from", "0.1.7", "013BEB0B51DA94546EEF2DFFC2A894F30FA2ED828EEB2CEAC6317F82B29F8C7B")]
    [InlineData("through", "2.3.8", "6580A473CF2F91C6752A01D2C31F729CB14F7E042B830BA46F8949F89E26BDB4")]
    public void CommittedMetadata_IsDocumentaryDataNotASourceProducer(string name, string version, string hash)
    {
        using var fixture = new AuditFixture();
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            "Fixtures", "collector-formats", name));
        var target = fixture.Full("test/documentary-fixtures/" + name);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));

        var errors = new List<string>();
        var packages = Collectors.Collect(Inputs(fixture), errors);
        errors.ShouldBeEmpty();
        packages.ShouldHaveSingleItem().Name.ShouldBe("baseline");
        File.Exists(Path.Combine(source, "package.json")).ShouldBeFalse();
        var bytes = File.ReadAllBytes(Path.Combine(source, "package.json.fixture"));
        Convert.ToHexString(SHA256.HashData(bytes)).ShouldBe(hash);
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "collector-formats", name, "package.json.fixture"))
            .ShouldBe(bytes);
        using var metadata = JsonDocument.Parse(bytes);
        metadata.RootElement.GetProperty("name").GetString().ShouldBe(name);
        metadata.RootElement.GetProperty("version").GetString().ShouldBe(version);
        metadata.RootElement.GetProperty("license").GetString().ShouldBe("MIT");
    }

    [Fact]
    public void ActualTestSourceProducer_StillRequiresItsLock()
    {
        using var fixture = new AuditFixture();
        fixture.Write("test/actual-producer/package.json", new { name = "actual-producer", version = "1.0.0" });
        var errors = new List<string>();
        Collectors.Collect(Inputs(fixture), errors);
        errors.ShouldHaveSingleItem().ShouldContain(fixture.Full("test/actual-producer/package-lock.json"));
    }

    [Theory]
    [InlineData("from", "0.1.7", false, "LICENSE.APACHE2", false)]
    [InlineData("from", "0.1.7", true, "LICENSE.APACHE2", false)]
    [InlineData("through", "2.3.8", false, "LICENSE.APACHE2", false)]
    [InlineData("through", "2.3.8", true, "LICENSE.APACHE2", false)]
    [InlineData("from", "0.1.7", false, "LICENSE.apache2", false)]
    [InlineData("from", "0.1.7", true, "LICENSE.apache2", false)]
    [InlineData("from", "0.1.7", false, "terms.ApAcHe2", true)]
    [InlineData("from", "0.1.7", true, "terms.ApAcHe2", true)]
    public void Apache2Document_IsCollectedWithoutApprovingShortTerms(
        string name, string version, bool archived, string path, bool declared)
    {
        using var fixture = new AuditFixture();
        var apache = Document(name, "LICENSE.APACHE2",
            "E8734448285A2DD773D40136ED5D5E8163A70701DD540CDC796CFCA232F67D55");
        var mit = Document(name, "LICENSE.MIT",
            "D72DEA1A8CDF3F4DFA2F594253D0C5B37BAEFC76E806F5ECB0E426393EDCD505");
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "collector-formats", name, "package.json.fixture")))!;
        var license = declared ? "SEE LICENSE IN " + path : "MIT";
        metadata["license"] = license;
        fixture.Npm(name, license, null, version);
        InstallOrArchive(fixture, name, version, metadata, new Dictionary<string, string>
        {
            [path] = apache,
            ["LICENSE.MIT"] = mit
        }, archived);

        var errors = new List<string>();
        var packages = Collectors.Collect(Inputs(fixture), errors);
        errors.ShouldBeEmpty();
        var package = packages.Single(item => item.Name == name);
        package.Version.ShouldBe(version);
        package.Texts.Count.ShouldBe(2);
        package.Texts.Single(file => file.Path.EndsWith("/" + path, StringComparison.Ordinal)).Text.ShouldBe(apache);
        package.Texts.Single(file => file.Path.EndsWith("/LICENSE.MIT", StringComparison.Ordinal)).Text.ShouldBe(mit);
        package.Metadata.ShouldBe(declared ? null : "MIT");
        LicenseText.Identify(apache).Licenses.ShouldBeEmpty();
        var report = fixture.Check();
        report.Packages.ShouldNotContain(item => item.Package == name);
        report.Errors.ShouldContain(error => error.Contains("unrecognized", StringComparison.Ordinal)
            && error.Contains(name, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, "LICENSE.APACHE2.bin")]
    [InlineData(true, "LICENSE.APACHE2.bin")]
    [InlineData(false, "LICENSE.APACHE2.dll")]
    [InlineData(true, "LICENSE.APACHE2.dll")]
    public void UnknownOrBinarySuffix_RemainsBlocking(bool archived, string path)
    {
        using var fixture = new AuditFixture();
        fixture.Npm("foreign", "MIT", null);
        InstallOrArchive(fixture, "foreign", "1.0.0",
            JsonNode.Parse("""{"name":"foreign","version":"1.0.0","license":"MIT"}""")!,
            new Dictionary<string, string> { [path] = AuditFixture.Mit }, archived);
        var errors = new List<string>();
        Collectors.Collect(Inputs(fixture), errors).ShouldNotContain(item => item.Name == "foreign");
        errors.ShouldContain(error => error.Contains("unsupported license entry", StringComparison.Ordinal)
            && error.Contains(path, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImplementationSuffix_IsNeverDocumentaryEvidence(bool archived)
    {
        using var fixture = new AuditFixture();
        fixture.Npm("foreign", "MIT", null);
        InstallOrArchive(fixture, "foreign", "1.0.0",
            JsonNode.Parse("""{"name":"foreign","version":"1.0.0","license":"MIT"}""")!,
            new Dictionary<string, string>
            {
                ["LICENSE.MIT"] = AuditFixture.Mit,
                ["LICENSE.APACHE2.js"] = "DO NOT READ IMPLEMENTATION"
            }, archived);
        var errors = new List<string>();
        var package = Collectors.Collect(Inputs(fixture), errors).Single(item => item.Name == "foreign");
        errors.ShouldBeEmpty();
        package.Texts.ShouldHaveSingleItem().Path.ShouldEndWith("/LICENSE.MIT");
        LicenseText.IsDocumentationPath("LICENSE.APACHE2.js").ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeclaredImplementationMasquerade_IsBlocking(bool archived)
    {
        using var fixture = new AuditFixture();
        const string declaration = "SEE LICENSE IN LICENSE.APACHE2.js";
        fixture.Npm("foreign", declaration, null);
        InstallOrArchive(fixture, "foreign", "1.0.0",
            JsonSerializer.SerializeToNode(new { name = "foreign", version = "1.0.0", license = declaration })!,
            new Dictionary<string, string> { ["LICENSE.APACHE2.js"] = AuditFixture.Mit }, archived);
        var errors = new List<string>();
        Collectors.Collect(Inputs(fixture), errors).ShouldNotContain(item => item.Name == "foreign");
        errors.ShouldContain(error => error.Contains("unsupported license entry", StringComparison.Ordinal)
            && error.Contains("LICENSE.APACHE2.js", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apache2Suffix_DoesNotHideProhibitedText(bool archived)
    {
        using var fixture = new AuditFixture();
        fixture.Npm("foreign", "MIT", null);
        InstallOrArchive(fixture, "foreign", "1.0.0",
            JsonNode.Parse("""{"name":"foreign","version":"1.0.0","license":"MIT"}""")!,
            new Dictionary<string, string>
            {
                ["LICENSE.MIT"] = AuditFixture.Mit,
                ["LICENSE.APACHE2"] = AuditFixture.Gpl
            }, archived);
        var errors = new List<string>();
        var packages = Collectors.Collect(Inputs(fixture), errors);
        errors.ShouldBeEmpty();
        packages.ShouldContain(item => item.Name == "foreign");
        fixture.Check().Errors.ShouldContain(error => error.Contains("prohibited GPL/AGPL/LGPL/SSPL", StringComparison.Ordinal));
    }

    internal static AuditInputs Inputs(AuditFixture fixture) => new(fixture.Root, fixture.Full("nuget.json"),
        fixture.Full("cache"), fixture.Full("artifacts/api"), fixture.Full("artifacts/cli"),
        fixture.Full("python-archives"), fixture.Full("npm-archives"));

    private static string Document(string name, string path, string hash)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "collector-formats", name, path));
        Convert.ToHexString(SHA256.HashData(bytes)).ShouldBe(hash);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void InstallOrArchive(AuditFixture fixture, string name, string version, JsonNode metadata,
        Dictionary<string, string> documents, bool archived)
    {
        var packagePath = ".github/scripts/node_modules/" + name;
        if (!archived)
        {
            fixture.WriteText(packagePath + "/package.json", metadata.ToJsonString());
            foreach (var (path, text) in documents) fixture.WriteText(packagePath + "/" + path, text);
            return;
        }
        Directory.Delete(fixture.Full(packagePath), true);
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionMode.Compress, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            foreach (var (path, text) in documents.Prepend(new KeyValuePair<string, string>("package.json", metadata.ToJsonString())))
            {
                using var content = new MemoryStream(Encoding.UTF8.GetBytes(text));
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "package/" + path) { DataStream = content });
            }
        }
        var digest = SHA512.HashData(bytes.ToArray());
        Directory.CreateDirectory(fixture.Full("npm-archives"));
        File.WriteAllBytes(fixture.Full("npm-archives/" + Convert.ToHexString(SHA256.HashData(digest)).ToLowerInvariant() + ".tgz"), bytes.ToArray());
        var lockPath = fixture.Full(".github/scripts/package-lock.json");
        var document = JsonNode.Parse(File.ReadAllText(lockPath))!;
        var record = document["packages"]!["node_modules/" + name]!;
        record["resolved"] = $"https://registry.npmjs.org/{name}/-/{name}-{version}.tgz";
        record["integrity"] = "sha512-" + Convert.ToBase64String(digest);
        File.WriteAllText(lockPath, document.ToJsonString());
    }
}
