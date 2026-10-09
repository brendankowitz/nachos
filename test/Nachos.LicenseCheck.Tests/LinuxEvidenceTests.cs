using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class LinuxEvidenceTests
{
    // Windows never reaches the native-descriptor FileStream; only a Linux run proves it can be read.
    [LinuxTheory]
    [InlineData("original")]
    [InlineData("stylesheet")]
    [InlineData("inline")]
    public async Task NativeOpenPathVerifiesReviewedPositiveInProcessAndThroughCli(string variant)
    {
        using var fixture = new ProtocolFixture(variant);
        fixture.Verify().OutputCount.ShouldBe(6);
        var report = fixture.Full("linux-report.json");
        var result = await ReportDestinationCliTests.Verify(fixture, report);
        result.Exit.ShouldBe(0, result.Error);
        File.ReadAllText(report).ShouldContain("\"outputCount\": 6");
    }
}
