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
    // count of descriptors open on the rejected objects flat. This is the one test using the internal open boundary:
    // through the verifier only a FIFO reaches reject-after-open (directories are refused before opening), and host
    // /dev/null and /dev/zero cannot be placed in an evidence tree without mount or mknod.
    [LinuxFact]
    public async Task RejectedOpensCloseTheirDescriptorsWithoutFinalizers()
    {
        var root = Directory.CreateTempSubdirectory("nlc-fd").FullName;
        try
        {
            var fifo = Path.Combine(root, "fifo");
            LinuxNodes.MakeFifo(fifo);
            var directory = Directory.CreateDirectory(Path.Combine(root, "directory")).FullName;
            string[] rejected = ["/dev/null", "/dev/zero", directory, fifo];
            var objects = rejected.Select(path => LinuxNodes.TryObject(path) ?? throw new IOException($"statx {path}")).ToHashSet();
            var before = OpenOn(objects);
            // Positive control: the counter must see a descriptor held on each object, so a link in the temporary
            // path (or any other path mismatch) cannot make the leak count vacuously zero.
            var held = rejected.Select(LinuxNodes.OpenReadOnly).ToArray();
            try { OpenOn(objects).ShouldBe(before + rejected.Length, "positive control: held descriptors must be counted"); }
            finally { foreach (var handle in held) handle.Dispose(); }
            OpenOn(objects).ShouldBe(before);
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
                    after = OpenOn(objects);
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

    // Counts this process's descriptors open on one of the objects, by (device, inode) rather than link text.
    private static int OpenOn(HashSet<(uint Major, uint Minor, ulong Inode)> objects) =>
        Directory.EnumerateFileSystemEntries("/proc/self/fd").Count(entry => LinuxNodes.TryObject(entry) is { } open && objects.Contains(open));
}
