using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Nachos.LicenseCheck;

internal sealed class ReportDestination
{
    private readonly string path;
    private readonly string[] roots;
    private readonly string[] files;
    private readonly bool audit;

    private ReportDestination(string path, string[] roots, string[] files, bool audit = false)
    {
        this.path = Path.GetFullPath(path);
        this.roots = roots;
        this.files = files;
        this.audit = audit;
        Validate();
    }

    public static ReportDestination ForDocs(string path, DocsProvenanceInputs inputs) =>
        new(path, [inputs.SiteRoot, inputs.AssetRoot, inputs.NpmRoot, inputs.OutputRoot], []);

    public static ReportDestination ForAudit(string path, AuditInputs inputs)
    {
        var roots = new List<string>
        {
            Path.Combine(inputs.Root, "docs", "site"), Path.Combine(inputs.Root, "docs", "assets"),
            inputs.DocsOutputRoot ?? Path.Combine(inputs.Root, "docs", "site", "dist"),
            inputs.ApiPublish, inputs.CliPublish, inputs.NugetCache, Path.Combine(inputs.Root, "eng", "licenses"),
            Path.Combine(inputs.Root, "src"), Path.Combine(inputs.Root, "test"), Path.Combine(inputs.Root, ".github")
        };
        if (inputs.PythonArchives is not null) roots.Add(inputs.PythonArchives);
        if (inputs.NpmArchives is not null) roots.Add(inputs.NpmArchives);
        return new ReportDestination(path, roots.ToArray(),
        [
            inputs.NugetInventory, Path.Combine(inputs.Root, "Nachos.slnx"),
            Path.Combine(inputs.Root, "THIRD-PARTY-NOTICES.md"),
            Path.Combine(inputs.Root, "eng", "license-exceptions.json"),
            Path.Combine(inputs.Root, "eng", "license-overrides.json"),
            Path.Combine(inputs.Root, "eng", "license-check", "allowlist.json")
        ], audit: true);
    }

    public void Write(string content)
    {
        Validate();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".license-report-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                writer.Write(content);
            // Replace the directory entry, never truncate an existing (possibly hardlinked) file.
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void Validate()
    {
        var destination = Canonical(path);
        if (audit && (destination.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
                .Contains("node_modules", StringComparer.OrdinalIgnoreCase)
            || IsAuditInputName(Path.GetFileName(destination))))
            throw new InvalidDataException("Report destination names collected dependency, lock or restore evidence.");
        if (Directory.Exists(path)) throw new InvalidDataException("Report destination is a directory.");
        foreach (var root in roots)
        {
            var protectedRoot = Canonical(root);
            if (destination.Equals(protectedRoot, StringComparison.OrdinalIgnoreCase)
                || destination.StartsWith(Path.TrimEndingDirectorySeparator(protectedRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Report destination overlaps a protected input/output root: {root}");
        }
        if (files.Any(file => destination.Equals(Canonical(file), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Report destination overlaps an authenticated input/evidence file.");
    }

    private static bool IsAuditInputName(string name) =>
        new[] { "package.json", "package-lock.json", "pnpm-lock.yaml", "pnpm-workspace.yaml", ".npmrc",
            "requirements.lock", "requirements.txt", "pyproject.toml", "project.assets.json",
            "yarn.lock", "uv.lock", "poetry.lock", "Pipfile.lock" }.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static string Canonical(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Any(character => character < 32 || "<>:\"|?*".Contains(character))
                || part.EndsWith('.') || part.EndsWith(' ')
                || Regex.IsMatch(part.Split('.')[0], @"\A(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("Report destination or protected root has an unsafe path alias.");
        }
        string? nearest = null;
        var missing = new List<string>();
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { if (nearest is null) missing.Add(Path.GetFileName(current)); continue; }
            catch (DirectoryNotFoundException) { if (nearest is null) missing.Add(Path.GetFileName(current)); continue; }
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new InvalidDataException($"Report destination or protected root contains a linked/device path: {current}");
            if (current != full && (attributes & FileAttributes.Directory) == 0)
                throw new InvalidDataException("Report destination parent is not a directory.");
            nearest ??= current;
        }
        var canonical = OperatingSystem.IsWindows() ? WindowsPath(nearest!) : nearest!;
        foreach (var part in missing.AsEnumerable().Reverse()) canonical = Path.Combine(canonical, part);
        return Path.TrimEndingDirectorySeparator(canonical);
    }

    private static string WindowsPath(string path)
    {
        using var handle = OpenPath(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException($"Report path canonicalization failed: {Marshal.GetLastPInvokeErrorMessage()}");
        var buffer = new char[512];
        var length = FinalPath(handle, buffer, (uint)buffer.Length, 1);
        if (length >= buffer.Length)
        {
            buffer = new char[checked((int)length + 1)];
            length = FinalPath(handle, buffer, (uint)buffer.Length, 1);
        }
        if (length == 0 || length >= buffer.Length)
            throw new IOException($"Report path canonicalization failed: {Marshal.GetLastPInvokeErrorMessage()}");
        return new string(buffer, 0, checked((int)length));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern SafeFileHandle OpenPath(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint FinalPath(SafeFileHandle file,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2, SizeParamIndex = 2)] char[] name, uint length, uint flags);
}
