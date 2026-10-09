namespace Nachos.LicenseCheck.Tests;

// Linux-only cases report as skipped elsewhere; returning early would count them as nominal passes.
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute() { if (!OperatingSystem.IsLinux()) Skip = LinuxOnly.Reason; }
}

public sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute() { if (!OperatingSystem.IsLinux()) Skip = LinuxOnly.Reason; }
}

internal static class LinuxOnly
{
    public const string Reason = "Not executed: Linux-only native open/statx evidence path.";
}
