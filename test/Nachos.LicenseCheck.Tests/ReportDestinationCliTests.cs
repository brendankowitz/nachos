using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class ReportDestinationCliTests
{
    [Theory]
    [InlineData("site/dist")]
    [InlineData("site")]
    [InlineData("assets")]
    [InlineData("site./dist/report.json")]
    [InlineData("site/dist/report.json.")]
    [InlineData("site/dist/report.json ")]
    [InlineData("site/dist/report.json")]
    [InlineData("site/dist/new-directory/report.json")]
    [InlineData("site/dist/.nachos/output-provenance.v1.json")]
    [InlineData("site/dist/.nachos/bundle-modules.json")]
    [InlineData("site/package.json")]
    [InlineData("site/src/index.html")]
    [InlineData("assets/logo.svg")]
    [InlineData("site/node_modules/fixture-content/package.json")]
    [InlineData("site/node_modules/fixture-content/client.js")]
    [InlineData("outside/../site/dist/report.json")]
    [InlineData("site/DIST/report.json")]
    public async Task VerifyDocsRejectsProtectedDestinationBeforeAnyWrite(string relative)
    {
        using var fixture = new ProtocolFixture();
        var before = Snapshot(fixture.Root);
        var result = await Verify(fixture, fixture.Full(relative));
        result.Exit.ShouldBe(2, result.Error);
        result.Error.ShouldContain("report", Case.Insensitive);
        Snapshot(fixture.Root).ShouldBe(before);
        (await Verify(fixture, fixture.Full("safe-report.json"))).Exit.ShouldBe(0);
    }

    [Theory]
    [InlineData("docs/site/dist/report.json")]
    [InlineData("docs/site/dist/new-directory/report.json")]
    [InlineData("docs/site/dist/.nachos/output-provenance.v1.json")]
    [InlineData("docs/site/package.json")]
    [InlineData("docs/site/src/index.html")]
    [InlineData("docs/assets/logo.svg")]
    [InlineData("docs/site/node_modules/site-baseline/package.json")]
    [InlineData("docs/site/../site/DIST/report.json")]
    [InlineData("artifacts/api/Nachos.Api.deps.json")]
    [InlineData("nuget.json")]
    [InlineData(".github/scripts/package-lock.json")]
    [InlineData("src/App/obj/project.assets.json")]
    [InlineData("eng/license-check/allowlist.json")]
    public async Task DocsAwareAuditRejectsAuthenticatedDestinationBeforeAnyWrite(string relative)
    {
        using var fixture = new AuditFixture();
        fixture.Docs();
        DocsFixtureBuilder.Seal(fixture);
        var before = Snapshot(fixture.Root);
        var result = await Audit(fixture, fixture.Full(relative));
        result.Exit.ShouldBe(2, result.Error);
        result.Error.ShouldContain("report", Case.Insensitive);
        Snapshot(fixture.Root).ShouldBe(before);
        (await Audit(fixture, fixture.Full("safe-report.json"))).Exit.ShouldBe(0);
    }

    [Theory]
    [InlineData("site-results/report.json")]
    [InlineData("assets-results/report.json")]
    [InlineData("outside/../reports/report.json")]
    public async Task ExternalPrefixSiblingAndExistingReportRemainSupported(string relative)
    {
        using var fixture = new ProtocolFixture();
        var report = fixture.Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, "previous report");
        var before = ProtectedSnapshot(fixture);
        (await Verify(fixture, report)).Exit.ShouldBe(0);
        File.ReadAllText(report).ShouldContain("\"outputCount\": 6");
        ProtectedSnapshot(fixture).ShouldBe(before);
        (await Verify(fixture, report)).Exit.ShouldBe(0);
        ProtectedSnapshot(fixture).ShouldBe(before);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LinkedReportParentOrTargetIsRejectedWithoutFollowingIt(bool parent)
    {
        using var fixture = new ProtocolFixture();
        var before = ProtectedSnapshot(fixture);
        var link = fixture.Full(parent ? "report-link" : "report-link.json");
        if (parent) Directory.CreateSymbolicLink(link, fixture.Full("site/dist"));
        else File.CreateSymbolicLink(link, fixture.Full("site/package.json"));
        try
        {
            var result = await Verify(fixture, parent ? Path.Combine(link, "new/report.json") : link);
            result.Exit.ShouldBe(2, result.Error);
            result.Error.ShouldContain("report", Case.Insensitive);
            ProtectedSnapshot(fixture).ShouldBe(before);
            (await Verify(fixture, fixture.Full("safe-report.json"))).Exit.ShouldBe(0);
        }
        finally
        {
            if (parent) Directory.Delete(link);
            else File.Delete(link);
        }
    }

    [Fact]
    public async Task ReplacingExternalHardlinkedReportDoesNotTruncateAuthenticatedInput()
    {
        using var fixture = new ProtocolFixture();
        var original = fixture.Full("site/package.json");
        var report = fixture.Full("external-report.json");
        HardLink(report, original);
        var before = ProtectedSnapshot(fixture);
        var result = await Verify(fixture, report);
        result.Exit.ShouldBe(0, result.Error);
        ProtectedSnapshot(fixture).ShouldBe(before);
        File.ReadAllText(report).ShouldContain("\"outputCount\": 6");
        (await Verify(fixture, fixture.Full("safe-report.json"))).Exit.ShouldBe(0);
    }

    [Fact]
    public async Task AuditExternalHardlinkedReportPreservesItsInventory()
    {
        using var fixture = new AuditFixture();
        fixture.Docs();
        DocsFixtureBuilder.Seal(fixture);
        var report = fixture.Full("external-report.json");
        var inventory = fixture.Full("nuget.json");
        HardLink(report, inventory);
        var hash = Hash(inventory);
        var result = await Audit(fixture, report);
        result.Exit.ShouldBe(0, result.Error);
        Hash(inventory).ShouldBe(hash);
        (await Audit(fixture, report)).Exit.ShouldBe(0);
    }

    [Fact]
    public async Task AuditDefaultArtifactsReportAndPrefixSiblingRemainSupported()
    {
        using var fixture = new AuditFixture();
        fixture.Docs();
        DocsFixtureBuilder.Seal(fixture);
        foreach (var relative in new[] { "eng/license-check/artifacts/license-report.json", "docs/site-results/report.json" })
        {
            var report = fixture.Full(relative);
            (await Audit(fixture, report)).Exit.ShouldBe(0);
            (await Audit(fixture, report)).Exit.ShouldBe(0);
        }
    }

    [Fact]
    public async Task FailedVerificationDoesNotReplaceExistingExternalReport()
    {
        using var fixture = new ProtocolFixture();
        fixture.Write("site/dist/unlisted.js", "unlisted");
        var report = fixture.Full("report.json");
        File.WriteAllText(report, "keep prior report");
        (await Verify(fixture, report)).Exit.ShouldBe(2);
        File.ReadAllText(report).ShouldBe("keep prior report");
    }

    [Fact]
    public async Task WindowsShortPathAliasCannotReachProtectedOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new ProtocolFixture();
        var original = fixture.Full("site/dist");
        var buffer = new char[32768];
        var length = ShortPath(original, buffer, (uint)buffer.Length);
        length.ShouldBeGreaterThan(0u);
        length.ShouldBeLessThan((uint)buffer.Length);
        var alias = new string(buffer, 0, checked((int)length));
        var before = Snapshot(fixture.Root);
        var result = await Verify(fixture, Path.Combine(alias, "report.json"));
        result.Exit.ShouldBe(2, result.Error);
        Snapshot(fixture.Root).ShouldBe(before);
    }

    [Fact]
    public async Task DocsAwareAuditRejectsLinkedParentBeforeCreatingDirectories()
    {
        using var fixture = new AuditFixture();
        fixture.Docs();
        DocsFixtureBuilder.Seal(fixture);
        var link = fixture.Full("outside-link");
        Directory.CreateSymbolicLink(link, fixture.Full("docs/site/dist"));
        try
        {
            var before = Snapshot(fixture.Full("docs"));
            var result = await Audit(fixture, Path.Combine(link, "new/report.json"));
            result.Exit.ShouldBe(2, result.Error);
            Snapshot(fixture.Full("docs")).ShouldBe(before);
        }
        finally { Directory.Delete(link); }
        (await Audit(fixture, fixture.Full("safe-report.json"))).Exit.ShouldBe(0);
    }

    [Theory]
    [InlineData("tools/build/package-lock.json")]
    [InlineData("tools/build/node_modules/extra-tool/package.json")]
    [InlineData("tools/build/node_modules/extra-tool/LICENSE")]
    public async Task AuditReportCannotOverwriteAnotherCollectedOrigin(string relative)
    {
        using var fixture = new AuditFixture();
        fixture.Docs();
        DocsFixtureBuilder.Seal(fixture);
        fixture.Npm("extra-tool", "MIT", AuditFixture.Mit, location: "tools/build");
        var before = Snapshot(fixture.Root);
        var result = await Audit(fixture, fixture.Full(relative));
        result.Exit.ShouldBe(2, result.Error);
        Snapshot(fixture.Root).ShouldBe(before);
        (await Audit(fixture, fixture.Full("safe-report.json"))).Exit.ShouldBe(0);
    }

    // A timeout kills the checker process so a hang fails the test instead of blocking the run.
    internal static Task<(int Exit, string Error)> Verify(ProtocolFixture fixture, string report, TimeSpan? timeout = null) => Run(
        timeout ?? TimeSpan.FromSeconds(45), "verify-docs", "--site-root", fixture.Full("site"), "--asset-root", fixture.Full("assets"),
        "--npm-root", fixture.Full("site"), "--output-root", fixture.Full("site/dist"), "--report", report);

    private static Task<(int Exit, string Error)> Audit(AuditFixture fixture, string report) => Run(TimeSpan.FromSeconds(45),
        "--repo", fixture.Root, "--nuget-inventory", fixture.Full("nuget.json"), "--nuget-cache", fixture.Full("cache"),
        "--api-publish", fixture.Full("artifacts/api"), "--cli-publish", fixture.Full("artifacts/cli"), "--report", report);

    private static async Task<(int Exit, string Error)> Run(TimeSpan timeout, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(LicenseAudit).Assembly.Location);
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(timeout); }
        catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
        await output;
        return (process.ExitCode, await error);
    }

    internal static string[] ProtectedSnapshot(ProtocolFixture fixture) =>
        Snapshot(fixture.Full("site")).Select(item => "site/" + item)
            .Concat(Snapshot(fixture.Full("assets")).Select(item => "assets/" + item)).ToArray();

    private static string[] Snapshot(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path) + ":" + (Directory.Exists(path) ? "directory" : Hash(path)))
        .Order(StringComparer.Ordinal).ToArray();

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    internal static void HardLink(string link, string original)
    {
        if (OperatingSystem.IsWindows()) CreateHardLink(link, original, IntPtr.Zero).ShouldBeTrue();
        else CreateUnixLink(original, link).ShouldBe(0);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string name, string existing, IntPtr security);

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint ShortPath(string path,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2, SizeParamIndex = 2)] char[] result, uint length);

    [DllImport("libc", EntryPoint = "link", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int CreateUnixLink([MarshalAs(UnmanagedType.LPUTF8Str)] string original, [MarshalAs(UnmanagedType.LPUTF8Str)] string link);
}
