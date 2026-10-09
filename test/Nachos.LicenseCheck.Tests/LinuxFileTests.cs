using Shouldly;

namespace Nachos.LicenseCheck.Tests;

// LinuxFile.EntryType decides whether an existing report destination may be replaced, so "absent" must mean
// ENOENT only and the entry itself (never a symlink's target) must be typed.
public sealed class LinuxFileTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("nlc-type");

    [LinuxFact]
    public void RegularFileIsTypedRegular()
    {
        var regular = Regular();
        LinuxFile.EntryType(regular).ShouldBe(LinuxFile.Regular);
    }

    [LinuxFact]
    public void MissingEntryIsAbsent() => LinuxFile.EntryType(Path.Combine(root.FullName, "missing.json")).ShouldBeNull();

    [LinuxFact]
    public void DanglingSymlinkIsTypedAsTheLinkItself()
    {
        var link = Path.Combine(root.FullName, "dangling.json");
        File.CreateSymbolicLink(link, Path.Combine(root.FullName, "missing.json"));
        LinuxFile.EntryType(link).ShouldBe(0xa000);
    }

    [LinuxFact]
    public void ErrorOtherThanNotFoundIsNotTreatedAsAbsent() =>
        Should.Throw<IOException>(() => LinuxFile.EntryType(Regular() + "/")).Message.ShouldContain("Cannot inspect path type", Case.Sensitive);

    public void Dispose() => root.Delete(recursive: true);

    private string Regular()
    {
        var path = Path.Combine(root.FullName, "regular.json");
        File.WriteAllText(path, "{}");
        return path;
    }
}
