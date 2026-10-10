using System.Security.Cryptography;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

// Report targets outside the protected roots whose existing entry is not a regular file. Every node lives in the
// test's own fixture directory; no host device path is ever used as a destination.
public sealed class LinuxReportDestinationTests
{
    private const string NonRegular = "LICENSE INPUT ERROR: Report destination exists and is not a regular file.";
    private static readonly TimeSpan HardTimeout = TimeSpan.FromSeconds(20);

    [LinuxTheory]
    [InlineData("fifo")]
    [InlineData("socket")]
    [InlineData("directory")]
    public async Task ExistingNonRegularTargetIsRejectedBeforeAnyWrite(string kind)
    {
        using var fixture = new ProtocolFixture();
        var target = fixture.Full("external/" + kind);
        Directory.CreateDirectory(fixture.Full("external"));
        if (kind == "fifo") LinuxNodes.MakeFifo(target);
        else if (kind == "socket") LinuxNodes.MakeSocket(target);
        else Directory.CreateDirectory(target);
        await RejectedUntouched(fixture, target, kind == "directory" ? "LICENSE INPUT ERROR: Report destination is a directory." : NonRegular);
    }

    [LinuxDeviceTheory]
    [InlineData(LinuxNodes.Character, 1u, 3u)]
    [InlineData(LinuxNodes.Block, 1u, 0u)]
    public async Task ExistingDeviceNodeTargetIsRejectedBeforeAnyWrite(int type, uint major, uint minor)
    {
        using var fixture = new ProtocolFixture();
        var target = fixture.Full("external/device");
        Directory.CreateDirectory(fixture.Full("external"));
        LinuxNodes.MakeDevice(target, type, major, minor);
        await RejectedUntouched(fixture, target, NonRegular);
    }

    [LinuxFact]
    public async Task ExternalRegularAndHardlinkedTargetsAreReplacedWithoutTouchingTheInput()
    {
        using var fixture = new ProtocolFixture();
        var input = fixture.Full("site/package.json");
        var alias = fixture.Full("external-alias.json");
        var regular = fixture.Full("external-report.json");
        ReportDestinationCliTests.HardLink(alias, input);
        File.WriteAllText(regular, "previous report");
        var inputBefore = (LinuxNodes.Identity(input), Hash(input));
        LinuxNodes.Identity(alias).Inode.ShouldBe(inputBefore.Item1.Inode);
        foreach (var report in new[] { alias, regular })
        {
            var result = await ReportDestinationCliTests.Verify(fixture, report, HardTimeout);
            result.Exit.ShouldBe(0, result.Error);
            File.ReadAllText(report).ShouldContain("\"outputCount\": 6");
            LinuxNodes.Identity(report).Type.ShouldBe(LinuxNodes.Regular);
        }
        LinuxNodes.Identity(alias).Inode.ShouldNotBe(inputBefore.Item1.Inode);
        (LinuxNodes.Identity(input), Hash(input)).ShouldBe(inputBefore);
    }

    private static async Task RejectedUntouched(ProtocolFixture fixture, string target, string message)
    {
        var identity = LinuxNodes.Identity(target);
        var directory = Path.GetDirectoryName(target)!;
        var entries = Directory.GetFileSystemEntries(directory).Order(StringComparer.Ordinal).ToArray();
        var protectedBefore = ReportDestinationCliTests.ProtectedSnapshot(fixture);
        var result = await ReportDestinationCliTests.Verify(fixture, target, HardTimeout);
        result.Exit.ShouldBe(2, result.Error);
        result.Error.ShouldContain(message);
        LinuxNodes.Identity(target).ShouldBe(identity);
        Directory.GetFileSystemEntries(directory).Order(StringComparer.Ordinal).ShouldBe(entries);
        ReportDestinationCliTests.ProtectedSnapshot(fixture).ShouldBe(protectedBefore);
        (await ReportDestinationCliTests.Verify(fixture, fixture.Full("safe-report.json"), HardTimeout)).Exit.ShouldBe(0);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
