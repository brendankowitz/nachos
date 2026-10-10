using System.Diagnostics;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

// Special files are created only in the test's own fixture or temporary directory. Host devices are opened
// read-only (nonblocking) and never read or written. Every check runs under a hard timeout so a blocking open
// fails the test instead of hanging the run.
public sealed class LinuxEvidenceTests
{
    internal const string Nonregular = "Nonregular or unverifiable evidence file";
    private static readonly TimeSpan HardTimeout = TimeSpan.FromSeconds(20);

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

    [LinuxTheory]
    [InlineData("site/tsconfig.json")]
    [InlineData(ProtocolFixture.Manifest)]
    [InlineData("site/dist/client.js")]
    public async Task FifoWithoutWriterIsRejectedWithoutBlocking(string relative)
    {
        using var fixture = new ProtocolFixture();
        var fifo = fixture.Full(relative);
        File.Delete(fifo);
        LinuxNodes.MakeFifo(fifo);
        var error = await Rejection(() => fixture.Verify(), fifo);
        error.ShouldBeOfType<InvalidDataException>().Message.ShouldContain("Docs provenance: " + Nonregular, Case.Sensitive);
    }

    // /dev/null and /dev/zero cannot be handed to the verifier without mount or mknod; LinuxDescriptorTests
    // type-checks them (and an opened directory) through the internal open boundary.
    [LinuxDeviceTheory]
    [InlineData(LinuxNodes.Block, 1u, 0u)]
    [InlineData(LinuxNodes.Character, 1u, 3u)]
    public async Task MintedDeviceNodesAreRejectedAsEvidence(int type, uint major, uint minor)
    {
        using var fixture = new ProtocolFixture();
        var node = fixture.Full("site/tsconfig.json");
        File.Delete(node);
        LinuxNodes.MakeDevice(node, type, major, minor);
        var message = (await Rejection(() => fixture.Verify())).ShouldBeOfType<InvalidDataException>().Message;
        // A node without a bound driver fails to open; one that opens must fail the type check.
        (message.Contains("Docs provenance: Cannot open evidence file", StringComparison.Ordinal)
            || message.Contains("Docs provenance: " + Nonregular, StringComparison.Ordinal)).ShouldBeTrue(message);
    }

    [LinuxFact]
    public async Task DirectoryInputIsRejectedBeforeOpening()
    {
        using var fixture = new ProtocolFixture();
        File.Delete(fixture.Full("site/tsconfig.json"));
        Directory.CreateDirectory(fixture.Full("site/tsconfig.json"));
        (await Rejection(() => fixture.Verify())).ShouldBeOfType<InvalidDataException>().Message
            .ShouldContain("Docs provenance: Expected regular file", Case.Sensitive);
    }

    [LinuxFact]
    public async Task SocketIsRejectedAsEvidence()
    {
        using var fixture = new ProtocolFixture();
        var socket = fixture.Full("site/tsconfig.json");
        File.Delete(socket);
        LinuxNodes.MakeSocket(socket);
        (await Rejection(() => fixture.Verify())).ShouldBeOfType<InvalidDataException>().Message
            .ShouldContain("Docs provenance: Cannot open evidence file", Case.Sensitive);
    }

    [LinuxFact]
    public async Task SymlinkToFifoIsRejectedAsLinkWithoutOpeningIt()
    {
        using var fixture = new ProtocolFixture();
        var fifo = fixture.Full("outside.fifo");
        LinuxNodes.MakeFifo(fifo);
        File.Delete(fixture.Full("site/tsconfig.json"));
        File.CreateSymbolicLink(fixture.Full("site/tsconfig.json"), fifo);
        (await Rejection(() => fixture.Verify(), fifo)).Message.ShouldContain("Docs provenance: Linked evidence path is not supported: tsconfig.json", Case.Sensitive);
    }

    internal static async Task Bounded(Action action, params string[] fifos)
    {
        var work = Task.Run(action);
        if (await Task.WhenAny(work, Task.Delay(HardTimeout)) != work)
        {
            // The blocking open is the failure under test; release the stranded reader so the run can continue.
            var release = Stopwatch.StartNew();
            while (!work.IsCompleted && release.Elapsed < TimeSpan.FromSeconds(5))
            {
                foreach (var fifo in fifos) LinuxNodes.ReleaseFifoReader(fifo);
                await Task.Delay(10);
            }
            throw new TimeoutException($"Special-file check did not finish within {HardTimeout.TotalSeconds:0} s (blocking open?).");
        }
        await work;
    }

    private static async Task<Exception> Rejection(Action action, params string[] fifos)
    {
        try { await Bounded(action, fifos); }
        catch (Exception exception) when (exception is not TimeoutException) { return exception; }
        throw new ShouldAssertException("The special file was accepted as evidence.");
    }
}
