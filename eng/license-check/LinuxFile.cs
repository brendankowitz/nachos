using System.Runtime.InteropServices;

namespace Nachos.LicenseCheck;

// Linux file-type primitives shared by evidence reads and report destinations. .NET never sets
// FileAttributes.Device on Unix, so FIFOs, sockets and device nodes are only visible through statx.
public static class LinuxFile
{
    public const int TypeMask = 0xf000;
    public const int Regular = 0x8000;
    private const int CurrentDirectory = -100;
    private const int NoFollow = 0x100;
    private const uint TypeField = 1;
    private const int NotFound = 2;

    /// <summary>Returns the S_IFMT bits of the entry itself (a final symlink is not followed), or null when it is absent.</summary>
    /// <exception cref="IOException">The entry exists but its type cannot be proven.</exception>
    public static int? EntryType(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("statx is Linux-only.");
        if (Statx(CurrentDirectory, path, NoFollow, TypeField, out var status) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == NotFound ? null : throw new IOException($"Cannot inspect path type: {path}: {Marshal.GetPInvokeErrorMessage(error)}");
        }
        return (status.Mask & TypeField) != 0 ? status.Mode & TypeMask : throw new IOException($"Path type is unavailable: {path}");
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out Status status);

    // Fixed Linux struct statx ABI (glibc >= 2.28): stx_mask at 0, stx_mode (u16) at 28.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Status
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }
}
