using System.Security.Cryptography;
using System.Text.Json;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class MicrosoftFixtureProvisioningTests
{
    [Theory]
    [InlineData("microsoft.data.sqlclient.sni.runtime.6.0.2.nupkg", "090B897E3658A11C5F734EDC4B350D08E39DCE6509BB4A75D09F96A5C1B6049D")]
    [InlineData("microsoft.data.sqlclient.sni.runtime.6.0.3.nupkg", "B9DF07C20101398F77CF16B209AFEFAFCC7190D6AC0B6E244E81A2FED4C96F5F")]
    [InlineData("microsoft.sqlserver.types.170.1000.7.nupkg", "CF5A138692BD7683A971D030013EDA563CD54CE472F52FFD62E96350FF1F9970")]
    [InlineData("owner-ms-license-6083936795.json", "7A843523FECD8769A984ED13CA94F6DAA891E90C5DDA873DA504BB9694736753")]
    public void StandardBuildCopiesExactFixtureBytes(string name, string expected)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "MicrosoftPrimaryEvidence", name);
        File.Exists(path).ShouldBeTrue("Standard restore/build must provision this fixture without private environment variables: " + name);
        using var stream = File.OpenRead(path);
        Convert.ToHexString(SHA256.HashData(stream)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Microsoft.Data.SqlClient.SNI.runtime", "6.0.2")]
    [InlineData("Microsoft.Data.SqlClient.SNI.runtime", "6.0.3")]
    [InlineData("Microsoft.SqlServer.Types", "170.1000.7")]
    public void StandardRestoreDownloadsFixturesWithoutAddingCompileOrRuntimeDependencies(string package, string version)
    {
        var assetsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "obj", "project.assets.json"));
        using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var framework = assets.RootElement.GetProperty("project").GetProperty("frameworks").EnumerateObject().Single().Value;
        framework.TryGetProperty("downloadDependencies", out var downloads).ShouldBeTrue();
        var matches = downloads.EnumerateArray().Where(item => item.GetProperty("name").GetString() == package).ToArray();
        matches.Length.ShouldBe(1, "Each fixture package requires one download item, including all retained versions.");
        var download = matches.Single();
        download.GetProperty("version").GetString()!.Replace(" ", "", StringComparison.Ordinal).Split(';')
            .ShouldContain($"[{version},{version}]");
        assets.RootElement.GetProperty("libraries").EnumerateObject()
            .ShouldNotContain(item => item.Name.StartsWith(package + "/", StringComparison.OrdinalIgnoreCase));
        foreach (var target in assets.RootElement.GetProperty("targets").EnumerateObject())
        {
            target.Value.EnumerateObject().ShouldNotContain(item => item.Name.StartsWith(package + "/", StringComparison.OrdinalIgnoreCase));
        }
        using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Nachos.LicenseCheck.Tests.deps.json")));
        deps.RootElement.GetProperty("libraries").EnumerateObject()
            .ShouldNotContain(item => item.Name.StartsWith(package + "/", StringComparison.OrdinalIgnoreCase));
        typeof(MicrosoftFixtureProvisioningTests).Assembly.GetReferencedAssemblies().ShouldNotContain(assembly => assembly.Name == package);
    }
}
