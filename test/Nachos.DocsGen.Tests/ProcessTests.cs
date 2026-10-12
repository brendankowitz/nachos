using System.Diagnostics;
using Shouldly;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Nachos.DocsGen.Tests;

public sealed class ProcessTests
{
    [Theory]
    [InlineData("nonzero")]
    [InlineData("stderr")]
    [InlineData("timeout")]
    [InlineData("flood")]
    [InlineData("pipes")]
    public async Task NonzeroTimeoutAndRedirectedStreamsAreBounded(string mode)
    {
        using var directory = new TestDirectory();
        var fixture = directory.Fixture(mode);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await Should.ThrowAsync<Exception>(() => CliHelp.CaptureAsync(fixture, directory.Root, TimeSpan.FromSeconds(1)));
            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(6));
        }
        finally
        {
            // The parent can exit before the inherited-pipe fixture does. Clean only its recorded PID.
            var pidFile = fixture + ".pid";
            if (File.Exists(pidFile))
            {
                var pid = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    using var child = Process.GetProcessById(pid);
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (ArgumentException) { /* The bounded fixture already exited. */ }
            }
        }
    }

    [Fact]
    public async Task ChildEnvironmentIsClearedAndUsesPrivateHome()
    {
        using var directory = new TestDirectory();
        Environment.SetEnvironmentVariable("DOCSGEN_SYNTHETIC_SECRET", "not-a-real-secret");
        try
        {
            var result = await CliHelp.CaptureAsync(directory.Fixture("environment"), directory.Root, TimeSpan.FromSeconds(3));
            result.Count.ShouldBe(1);
            result[""].ShouldContain("First-party process test.");
        }
        finally { Environment.SetEnvironmentVariable("DOCSGEN_SYNTHETIC_SECRET", null); }
    }

    [Fact]
    public async Task AbsentCliCannotProduceSuccessShapedHelp()
    {
        using var directory = new TestDirectory();
        await Should.ThrowAsync<InvalidDataException>(() =>
            CliHelp.CaptureAsync(Path.Combine(directory.Root, "absent.dll"), directory.Root, TimeSpan.FromSeconds(1)));
    }
}
