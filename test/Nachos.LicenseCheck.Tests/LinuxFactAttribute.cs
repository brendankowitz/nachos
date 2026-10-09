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

// Device-node fixtures need CAP_MKNOD; they run only on explicit opt-in so an unprivileged or policy-restricted
// host reports them as not executed instead of a nominal pass. Once opted in, a refused mknod fails the test.
public sealed class LinuxDeviceTheoryAttribute : TheoryAttribute
{
    public LinuxDeviceTheoryAttribute() { Skip = LinuxOnly.DeviceSkip; }
}

internal static class LinuxOnly
{
    public const string Reason = "Not executed: Linux-only native open/statx evidence path.";
    public const string DeviceOptIn = "NACHOS_LINUX_DEVICE_FIXTURES";

    public static string? DeviceSkip => !OperatingSystem.IsLinux() ? Reason
        : Environment.GetEnvironmentVariable(DeviceOptIn) == "1" ? null
        : $"Not executed: creating device nodes (mknod, CAP_MKNOD) in the test's own directory requires {DeviceOptIn}=1.";
}
