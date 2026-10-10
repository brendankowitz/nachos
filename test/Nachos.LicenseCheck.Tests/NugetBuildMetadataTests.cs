using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Nachos.LicenseCheck;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class NugetBuildMetadataTests
{
    [Theory]
    [InlineData("2.25.29", "2.25.29+RR")]
    [InlineData("1.2.3", "1.2.3+0")]
    [InlineData("0.0.0", "0.0.0+000.abc-XYZ")]
    [InlineData("1.2.3-alpha.1", "1.2.3-alpha.1+sha.004.a-b")]
    [InlineData("1.2.3-0", "1.2.3-0+build")]
    [InlineData("1.2.3-01a", "1.2.3-01a+build")]
    [InlineData("2147483647.2147483647.2147483647", "2147483647.2147483647.2147483647+build")]
    public void BuildMetadataOnlyDifference_PreservesRawInventoryAndArchive(string inventory, string nuspec)
    {
        using var fixture = new AuditFixture();
        var archive = Package(fixture, inventory, nuspec);
        var inventoryBytes = File.ReadAllBytes(fixture.Full("nuget.json"));
        var archiveBytes = File.ReadAllBytes(archive);
        var errors = new List<string>();
        var packages = Collectors.Collect(Apache2DocumentTests.Inputs(fixture), errors);
        errors.ShouldBeEmpty();
        var package = packages.Single(item => item.Name == "example");
        package.Version.ShouldBe(inventory);
        package.Origin.ShouldBe(fixture.Full("nuget.json"));
        package.Archive.ShouldBe(archive);
        File.ReadAllBytes(package.Origin).ShouldBe(inventoryBytes);
        File.ReadAllBytes(package.Archive!).ShouldBe(archiveBytes);
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("2.25.29", "2.25.30+RR")]
    [InlineData("2.25.29", "2.25.29-beta+RR")]
    [InlineData("2.25.29-beta", "2.25.29+RR")]
    [InlineData("2.25.29-beta", "2.25.29-Beta+RR")]
    [InlineData("2.25.29", "2.25.29+")]
    [InlineData("2.25.29", "2.25.29+.RR")]
    [InlineData("2.25.29", "2.25.29+RR.")]
    [InlineData("2.25.29", "2.25.29+R..R")]
    [InlineData("2.25.29", "2.25.29+R+R")]
    [InlineData("2.25.29", "2.25.29+R R")]
    [InlineData("2.25.29", "2.25.29+R_R")]
    [InlineData("2.25.29", " 2.25.29+RR")]
    [InlineData("2.25.29", "2.25.29+RR ")]
    [InlineData("2.25.29", "2.25.29+RR\n")]
    [InlineData("2.25.29", "2.25.29+\u00e9")]
    [InlineData("02.25.29", "02.25.29+RR")]
    [InlineData("2.025.29", "2.025.29+RR")]
    [InlineData("2.25.029", "2.25.029+RR")]
    [InlineData("2.25", "2.25+RR")]
    [InlineData("2.25.29.0", "2.25.29.0+RR")]
    [InlineData("2.25.29-alpha.01", "2.25.29-alpha.01+RR")]
    [InlineData("2.25.29-alpha..1", "2.25.29-alpha..1+RR")]
    [InlineData("2.25.29-", "2.25.29-+RR")]
    [InlineData("2.25.29-a_b", "2.25.29-a_b+RR")]
    [InlineData("2.25.29+old", "2.25.29+new")]
    [InlineData("2.25.29+RR", "2.25.29")]
    [InlineData("2147483648.0.0", "2147483648.0.0+RR")]
    [InlineData("0.2147483648.0", "0.2147483648.0+RR")]
    [InlineData("0.0.2147483648", "0.0.2147483648+RR")]
    public void OtherVersionDifferencesOrMalformedSuffixes_AreIdentityFailures(string inventory, string nuspec)
    {
        using var fixture = new AuditFixture();
        Package(fixture, inventory, nuspec);
        var errors = new List<string>();
        Collectors.Collect(Apache2DocumentTests.Inputs(fixture), errors).ShouldNotContain(item => item.Name == "example");
        errors.ShouldContain(error => error.Contains("nupkg identity disagrees with resolved inventory", StringComparison.Ordinal));
    }

    [Fact]
    public void CapturedStreamJsonRpcNuspec_CollectsWithoutInventingLicenseText()
    {
        using var fixture = new AuditFixture();
        fixture.Nuget("StreamJsonRpc", "MIT", null, version: "2.25.29");
        var archive = fixture.Full("cache/streamjsonrpc/2.25.29/streamjsonrpc.2.25.29.nupkg");
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "collector-formats", "streamjsonrpc", "StreamJsonRpc.nuspec"));
        Convert.ToHexString(SHA256.HashData(bytes)).ShouldBe("DED76E786E0F2F58F33C9A56E06CF1D0F564A674F01947714FFFAED23B0098F7");
        ReplaceNuspec(archive, "StreamJsonRpc.nuspec", bytes);
        var before = File.ReadAllBytes(archive);
        var errors = new List<string>();
        var packages = Collectors.Collect(Apache2DocumentTests.Inputs(fixture), errors);
        errors.ShouldBeEmpty();
        var package = packages.Single(item => item.Name == "StreamJsonRpc");
        package.Version.ShouldBe("2.25.29");
        package.Metadata.ShouldBe("MIT");
        package.Texts.ShouldBeEmpty();
        File.ReadAllBytes(archive).ShouldBe(before);
        fixture.Check().Errors.ShouldContain(error => error.Contains("license text unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildMetadataCannotHidePackageNameMismatch()
    {
        using var fixture = new AuditFixture();
        var archive = Package(fixture, "2.25.29", "2.25.29+RR");
        ReplaceNuspec(archive, "example.nuspec", Encoding.UTF8.GetBytes(
            "<package><metadata><id>different</id><version>2.25.29+RR</version><license type=\"expression\">MIT</license></metadata></package>"));
        var errors = new List<string>();
        Collectors.Collect(Apache2DocumentTests.Inputs(fixture), errors).ShouldNotContain(item => item.Name == "example");
        errors.ShouldContain(error => error.Contains("nupkg identity disagrees with resolved inventory", StringComparison.Ordinal));
    }

    private static string Package(AuditFixture fixture, string inventory, string nuspec)
    {
        fixture.Nuget("example", "MIT", AuditFixture.Mit, version: inventory);
        var archive = fixture.Full($"cache/example/{inventory}/example.{inventory}.nupkg");
        var document = new XDocument(new XElement("package", new XElement("metadata",
            new XElement("id", "example"), new XElement("version", nuspec),
            new XElement("license", new XAttribute("type", "expression"), "MIT"))));
        ReplaceNuspec(archive, "example.nuspec", Encoding.UTF8.GetBytes(document.ToString()));
        return archive;
    }

    private static void ReplaceNuspec(string archive, string path, byte[] bytes)
    {
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Update);
        zip.GetEntry(path)!.Delete();
        using var stream = zip.CreateEntry(path).Open();
        stream.Write(bytes);
    }
}
