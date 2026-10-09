using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

// Disposable special-file fixtures. Nodes are created only inside the calling test's own directory;
// device nodes are minted there with mknod (opt-in) and never refer to a host /dev path.
internal static class LinuxNodes
{
    public const int Fifo = 0x1000, Character = 0x2000, Directory = 0x4000, Block = 0x6000, Regular = 0x8000, Socket = 0xc000;
    private const int WriteOnlyNonblocking = 0x1 | 0x800;

    public static void MakeFifo(string path) => Succeeded(CreateFifo(path, 0x180), "mkfifo", path);

    public static void MakeDevice(string path, int type, uint major, uint minor) =>
        Succeeded(CreateNode(path, (uint)type | 0x180, (ulong)(major & 0xfff) << 8 | ((ulong)major & ~0xfffUL) << 32
            | (minor & 0xff) | ((ulong)minor & ~0xffUL) << 12), "mknod", path);

    // sun_path is limited to 108 bytes, so bind under a short temporary directory and rename the node into place.
    public static void MakeSocket(string path)
    {
        var shortDirectory = System.IO.Directory.CreateTempSubdirectory("nlc");
        try
        {
            var bound = Path.Combine(shortDirectory.FullName, "s");
            // .NET unlinks the bound path when the socket is disposed, so move the node first.
            using var socket = new System.Net.Sockets.Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(bound));
            File.Move(bound, path);
        }
        finally { shortDirectory.Delete(recursive: true); }
    }

    // A reader blocked in open() on a FIFO is released once a writer opens it; used only to clean up a hung mutant.
    public static void ReleaseFifoReader(string fifo)
    {
        var descriptor = Open(fifo, WriteOnlyNonblocking);
        if (descriptor >= 0) new SafeFileHandle(descriptor, ownsHandle: true).Dispose();
    }

    public static (int Type, uint Major, uint Minor, ulong Inode) Identity(string path)
    {
        Succeeded(Statx(-100, path, 0x100, 0x1 | 0x100, out var status), "statx", path);
        return (status.Mode & 0xf000, status.DeviceMajor, status.DeviceMinor, status.Inode);
    }

    private static void Succeeded(int result, string call, string path) =>
        result.ShouldBe(0, $"{call} {path}: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int CreateFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", EntryPoint = "mknod", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int CreateNode([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode, ulong device);

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out Status status);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Status
    {
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }
}
