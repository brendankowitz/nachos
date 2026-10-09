using System.Runtime;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

[CollectionDefinition(nameof(LinuxDescriptorTests), DisableParallelization = true)]
public sealed class LinuxDescriptorRunsAlone;

// Runs alone: the no-GC region is process-wide and parallel tests would exhaust its allocation budget.
[Collection(nameof(LinuxDescriptorTests))]
public sealed class LinuxDescriptorTests
{
    private const int Rounds = 250;

    // Inside a no-GC region finalizers cannot close a leaked SafeFileHandle, so only explicit disposal keeps the
    // count of descriptors open on the rejected objects flat.
    [LinuxFact]
    public async Task RejectedOpensCloseTheirDescriptorsWithoutFinalizers()
    {
        var root = Directory.CreateTempSubdirectory("nlc-fd").FullName;
        try
        {
            var fifo = Path.Combine(root, "fifo");
            LinuxNodes.MakeFifo(fifo);
            var directory = Directory.CreateDirectory(Path.Combine(root, "directory")).FullName;
            string[] rejected = [fifo, directory, "/dev/null", "/dev/zero"];
            var before = OpenOn(rejected);
            var after = -1;
            await LinuxEvidenceTests.Bounded(() =>
            {
                GC.TryStartNoGCRegion(64 * 1024 * 1024).ShouldBeTrue();
                try
                {
                    for (var round = 0; round < Rounds; round++)
                        foreach (var path in rejected)
                            Should.Throw<InvalidDataException>(() => DocsProvenance.OpenRegular(path).Dispose())
                                .Message.ShouldContain(LinuxEvidenceTests.Nonregular, Case.Sensitive);
                    after = OpenOn(rejected);
                    GCSettings.LatencyMode.ShouldBe(GCLatencyMode.NoGCRegion, "a GC ran, so finalizers could hide a leak");
                }
                finally
                {
                    if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) GC.EndNoGCRegion();
                }
            }, fifo);
            after.ShouldBe(before, $"descriptors left open on rejected objects after {Rounds * rejected.Length} rejections");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Counts this process's descriptors whose /proc link names one of the rejected objects.
    private static int OpenOn(string[] paths) => Directory.EnumerateFileSystemEntries("/proc/self/fd")
        .Select(entry => { try { return new FileInfo(entry).LinkTarget; } catch (IOException) { return null; } })
        .Count(target => target is not null && paths.Contains(target, StringComparer.Ordinal));
}
