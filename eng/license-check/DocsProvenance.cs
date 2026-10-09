using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Nachos.LicenseCheck;

public sealed record DocsProvenanceInputs(string SiteRoot, string AssetRoot, string NpmRoot, string OutputRoot,
    string Site = "https://brendankowitz.github.io", string Base = "/nachos");
public sealed record DocsPackage(string Package, string Version);
public sealed record DocsProvenanceReport(int OutputCount, int InputCount, int SourceCount,
    IReadOnlyList<DocsPackage> Packages, string ManifestSha256);

public static class DocsProvenance
{
    public const string ManifestPath = ".nachos/output-provenance.v1.json";
    public const string FlatPath = ".nachos/bundle-modules.json";
    private static readonly string[] RequiredFiles = ["package.json", "package-lock.json", "astro.config.mjs", "tsconfig.json", ".npmrc"];
    private static readonly string[] RequiredTrees = ["src", "public", "integrations", "scripts"];
    private static readonly HashSet<string> BoundRoles = new(StringComparer.Ordinal)
        { "copy", "vite-chunk", "vite-asset", "expressive-stylesheet", "expressive-script", "sitemap", "pagefind" };
    private static readonly HashSet<string> IntermediateRoles = new(StringComparer.Ordinal)
        { "astro-render", "expressive-html", "expressive-inline-style", "mermaid-svg" };

    public static DocsProvenanceReport Verify(DocsProvenanceInputs inputs)
    {
        try { return Validate(inputs); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException
            or EntryPointNotFoundException or DllNotFoundException)
        {
            throw new InvalidDataException("Docs provenance: " + exception.Message, exception);
        }
    }

    private static DocsProvenanceReport Validate(DocsProvenanceInputs inputs)
    {
        var paths = new EvidencePaths();
        foreach (var root in new[] { inputs.SiteRoot, inputs.AssetRoot, inputs.NpmRoot, inputs.OutputRoot })
            EvidencePaths.Root(root);
        var manifestFile = paths.File(inputs.OutputRoot, ManifestPath);
        using var manifest = ReadJson(manifestFile);
        var document = manifest.RootElement;
        Fields(document, "schemaVersion", "build", "inputs", "sources", "outputs");
        if (!document.GetProperty("schemaVersion").TryGetInt32(out var version) || version != 1)
            throw new InvalidDataException("Unsupported schemaVersion; expected integer 1.");
        var build = document.GetProperty("build");
        Fields(build, "site", "base");
        if (Text(build, "site") != inputs.Site || Text(build, "base") != inputs.Base)
            throw new InvalidDataException("Build settings differ from externally selected site/base.");

        var actualInputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in RequiredFiles)
            actualInputs.Add("site/" + file, HashFile(paths.File(inputs.SiteRoot, file)));
        foreach (var tree in RequiredTrees)
            foreach (var file in paths.Tree(inputs.SiteRoot, tree))
                actualInputs.Add("site/" + file, HashFile(paths.File(inputs.SiteRoot, file)));
        foreach (var file in paths.Tree(inputs.AssetRoot, ""))
            actualInputs.Add("assets/" + file, HashFile(paths.File(inputs.AssetRoot, file)));
        var recordedInputs = ReadPaths(document.GetProperty("inputs"), "inputs", "path", "sha256");
        EqualClosure(recordedInputs, actualInputs, "input");

        var actualOutputs = paths.Tree(inputs.OutputRoot, "")
            .Where(path => path is not (ManifestPath or FlatPath))
            .ToDictionary(path => path, path => HashFile(paths.File(inputs.OutputRoot, path)), StringComparer.Ordinal);
        if (actualOutputs.Count == 0) throw new InvalidDataException("Output closure is empty or manifest-only.");
        var outputs = ReadPaths(document.GetProperty("outputs"), "outputs", "path", "sha256", "evidence", "packages");
        EqualClosure(outputs, actualOutputs, "output");

        // Keep the existing lock identity/closure rules, but reject JSON duplicate fields first.
        using var lockDocument = ReadJson(paths.File(inputs.SiteRoot, "package-lock.json"));
        using var sitePackage = ReadJson(paths.File(inputs.SiteRoot, "package.json"));
        var locked = NpmLock.Read(paths.File(inputs.SiteRoot, "package-lock.json"));
        var sourceRecords = Array(document.GetProperty("sources"), "sources");
        var sources = ReadPaths(document.GetProperty("sources"), "sources");
        var sourcePackages = new Dictionary<string, DocsPackage?>(StringComparer.Ordinal);
        foreach (var record in sourceRecords)
        {
            var path = Text(record, "path");
            var kind = Text(record, "kind");
            if (kind == "first-party")
            {
                Fields(record, "path", "sha256", "kind");
                if (!actualInputs.TryGetValue(path, out var expected) || expected != Hash(record))
                    throw new InvalidDataException($"First-party source is outside the fixed input closure: {path}");
                sourcePackages.Add(path, null);
                continue;
            }
            Fields(record, "path", "sha256", "kind", "package", "version", "packagePath", "packageJsonSha256");
            if (kind != "package" || !path.StartsWith("npm/", StringComparison.Ordinal))
                throw new InvalidDataException($"Unknown source kind or namespace: {path}");
            var packagePath = SafePath(Text(record, "packagePath"));
            if (!Regex.IsMatch(packagePath, @"\Anode_modules/(?:@[a-z0-9._-]+/)?[a-z0-9][a-z0-9._-]*(?:/node_modules/(?:@[a-z0-9._-]+/)?[a-z0-9][a-z0-9._-]*)*\z")
                || !path.StartsWith("npm/" + packagePath + "/", StringComparison.Ordinal))
                throw new InvalidDataException($"Package source ownership path disagrees: {path}");
            var sourceFile = paths.File(inputs.NpmRoot, path[4..]);
            if (HashFile(sourceFile) != Hash(record)) throw new InvalidDataException($"Source hash differs: {path}");
            var directory = Path.GetDirectoryName(sourceFile)!;
            var packageRoot = paths.Directory(inputs.NpmRoot, packagePath);
            while (!System.IO.File.Exists(Path.Combine(directory, "package.json")))
            {
                if (Collectors.PathComparer.Equals(directory, packageRoot))
                    throw new InvalidDataException($"Package owner manifest missing: {path}");
                directory = Path.GetDirectoryName(directory)!;
            }
            if (!Collectors.PathComparer.Equals(directory, packageRoot))
                throw new InvalidDataException($"Nearest manifest owner differs from packagePath: {path}");
            var packageJson = paths.File(inputs.NpmRoot, packagePath + "/package.json");
            using var package = ReadJson(packageJson);
            var name = Text(package.RootElement, "name");
            var packageVersion = Text(package.RootElement, "version");
            if (HashFile(packageJson) != Hash(record, "packageJsonSha256")
                || name != Text(record, "package") || packageVersion != Text(record, "version")
                || !locked.TryGetValue(packagePath, out var lockRecord)
                || lockRecord.TryGetProperty("link", out var link) && link.ValueKind != JsonValueKind.False
                || Text(lockRecord, "version") != packageVersion
                || (lockRecord.TryGetProperty("name", out var lockName) ? String(lockName) : NpmLock.Name(packagePath)) != name)
                throw new InvalidDataException($"Package source/nearest manifest/lock identity differs: {path}");
            sourcePackages.Add(path, new DocsPackage(name, packageVersion));
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var union = new HashSet<DocsPackage>();
        foreach (var output in Array(document.GetProperty("outputs"), "outputs"))
        {
            var outputPath = Text(output, "path");
            var hash = Hash(output);
            var evidence = Array(output.GetProperty("evidence"), "evidence");
            if (evidence.Length == 0) throw new InvalidDataException($"Output evidence is empty: {outputPath}");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var outputPackages = new HashSet<DocsPackage>();
            var bound = new List<string>();
            var intermediates = new List<string>();
            foreach (var record in evidence)
            {
                Fields(record, "producer", "sources", "sha256");
                var role = Text(record, "producer");
                var evidenceHash = Hash(record);
                var references = Array(record.GetProperty("sources"), "source references").Select(String).ToArray();
                Sorted(references, "source references", paths: true);
                if (references.Length == 0) throw new InvalidDataException("Evidence source references are empty.");
                var key = JsonSerializer.Serialize(new { role, references, evidenceHash });
                if (!seen.Add(key)) throw new InvalidDataException($"Semantic duplicate evidence: {outputPath}");
                foreach (var reference in references)
                {
                    if (!sourcePackages.TryGetValue(reference, out var package))
                        throw new InvalidDataException($"Evidence references missing source: {reference}");
                    used.Add(reference);
                    if (package is not null) outputPackages.Add(package);
                }
                if (BoundRoles.Contains(role))
                {
                    bound.Add(role);
                    if (evidenceHash != hash) throw new InvalidDataException($"Output-bound evidence hash differs: {role}/{outputPath}");
                }
                else if (IntermediateRoles.Contains(role)) intermediates.Add(role);
                else throw new InvalidDataException($"Unknown producer role: {role}");
                if (role == "copy" && (references.Length != 1 || sources[references[0]] != evidenceHash))
                    throw new InvalidDataException($"Copy evidence must match its single source and output: {outputPath}");
            }
            if (intermediates.Count == 0 ? bound.Count != 1
                : intermediates.Count(role => role == "astro-render") != 1
                    || !(bound.Count == 0 && outputPath.EndsWith(".html", StringComparison.Ordinal)
                        || bound.Count == 1 && bound[0] == "pagefind"))
                throw new InvalidDataException($"Invalid rendering/output-bound evidence context: {outputPath}");
            var claimed = Packages(output.GetProperty("packages"));
            if (!outputPackages.SetEquals(claimed))
                throw new InvalidDataException($"Output package union differs from referenced sources: {outputPath}");
            union.UnionWith(outputPackages);
        }
        if (!used.SetEquals(sources.Keys)) throw new InvalidDataException("Source closure contains unreferenced sources.");
        using var flat = ReadJson(paths.File(inputs.OutputRoot, FlatPath));
        if (!union.SetEquals(Packages(flat.RootElement)))
            throw new InvalidDataException("Legacy flat package union differs from complete output evidence.");
        return new DocsProvenanceReport(outputs.Count, recordedInputs.Count, sources.Count,
            union.OrderBy(package => package.Package, StringComparer.Ordinal).ThenBy(package => package.Version, StringComparer.Ordinal).ToArray(),
            HashFile(manifestFile));
    }

    private static Dictionary<string, string> ReadPaths(JsonElement array, string context, params string[] fields)
    {
        var items = Array(array, context);
        Sorted(items.Select(item => Text(item, "path")).ToArray(), context, paths: true);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (fields.Length > 0) Fields(item, fields);
            result.Add(Text(item, "path"), Hash(item));
        }
        return result;
    }

    private static void EqualClosure(Dictionary<string, string> recorded, Dictionary<string, string> actual, string kind)
    {
        if (recorded.Count != actual.Count || recorded.Any(item => !actual.TryGetValue(item.Key, out var hash) || hash != item.Value))
            throw new InvalidDataException($"Exact {kind} closure differs (missing, added or changed path/hash).");
    }

    private static DocsPackage[] Packages(JsonElement value)
    {
        var result = Array(value, "packages").Select(item =>
        {
            Fields(item, "package", "version");
            return new DocsPackage(Text(item, "package"), Text(item, "version"));
        }).ToArray();
        Sorted(result.Select(item => item.Package + "\0" + item.Version).ToArray(), "packages");
        return result;
    }

    private static void Sorted(string[] values, string context, bool paths = false)
    {
        string? previous = null;
        var aliases = new HashSet<string>(paths ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (paths) SafePath(value);
            if (previous is not null && string.CompareOrdinal(previous, value) >= 0 || !aliases.Add(value))
                throw new InvalidDataException($"Unsorted, duplicate or case-aliased {context}.");
            previous = value;
        }
    }

    private static string SafePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Any(character => character > 127 || character < 32 || "<>:\"\\|?*".Contains(character)))
            throw new InvalidDataException($"Unsafe path or unsupported non-ASCII path: {path}");
        foreach (var segment in path.Split('/'))
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
                || Regex.IsMatch(segment.Split('.')[0], @"\A(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException($"Unsafe path segment: {path}");
        return path;
    }

    private static string Text(JsonElement element, string property) => String(element.GetProperty(property));
    private static string String(JsonElement value) => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
        ? value.GetString()! : throw new InvalidDataException("Expected nonempty string.");
    private static string Hash(JsonElement value, string property = "sha256")
    {
        var text = Text(value, property);
        if (!Regex.IsMatch(text, @"\A[0-9a-f]{64}\z")) throw new InvalidDataException("Expected lowercase SHA256.");
        return text;
    }
    private static JsonElement[] Array(JsonElement value, string context) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().ToArray() : throw new InvalidDataException($"Expected array: {context}");
    private static void Fields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(fields.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Unexpected/missing/duplicate object fields.");
    }
    private static JsonDocument ReadJson(string path)
    {
        using var stream = OpenRegular(path);
        var document = JsonDocument.Parse(stream);
        try { UniqueFields(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }
    private static void UniqueFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject())
            {
                if (!names.Add(item.Name)) throw new InvalidDataException($"JSON duplicate field: {item.Name}");
                UniqueFields(item.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) UniqueFields(item);
    }
    private static string HashFile(string path)
    {
        using var stream = OpenRegular(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static FileStream OpenRegular(string path)
    {
        FileStream stream;
        if (OperatingSystem.IsWindows()) stream = System.IO.File.OpenRead(path);
        else
        {
            // Opening a FIFO must not wait for a writer before we can reject it.
            if (!OperatingSystem.IsLinux())
                throw new InvalidDataException("Non-Windows regular-file verification requires Linux statx; other platforms are not yet supported.");
            var descriptor = OpenNonblocking(path, 0x800);
            if (descriptor < 0) throw new IOException($"Cannot open evidence file: {path}: {Marshal.GetLastPInvokeErrorMessage()}");
            var handle = new SafeFileHandle(descriptor, ownsHandle: true);
            try
            {
                // statx has a fixed Linux ABI. Seekable devices are not regular files.
                if (Statx(handle, "", 0x1000, 1, out var status) != 0 || (status.Mask & 1) == 0 || (status.Mode & 0xf000) != 0x8000)
                    throw new InvalidDataException($"Nonregular or unverifiable evidence file: {path}");
                // open() returned a synchronous descriptor; FileStream rejects isAsync for it on Unix.
                stream = new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false);
            }
            catch { handle.Dispose(); throw; }
        }
        if (stream.CanSeek) return stream;
        stream.Dispose();
        throw new InvalidDataException($"Nonregular evidence file: {path}");
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int OpenNonblocking([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Statx(SafeFileHandle descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxFileStatus status);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }

    private sealed class EvidencePaths
    {
        private readonly Dictionary<string, Dictionary<string, string>> entries = new(Collectors.PathComparer);

        public static void Root(string root)
        {
            var directory = new DirectoryInfo(Path.GetFullPath(root));
            for (var current = directory; current is not null; current = current.Parent)
                Check(current.FullName, directory: true);
        }

        public string File(string root, string path) => Resolve(root, path, directory: false);
        public string Directory(string root, string path) => Resolve(root, path, directory: true);
        private string Resolve(string root, string path, bool directory)
        {
            SafePath(path);
            var full = Collectors.Under(root, path);
            var current = Path.GetFullPath(root);
            var segments = path.Split('/');
            for (var index = 0; index < segments.Length; index++)
            {
                var names = Entries(current);
                if (!names.TryGetValue(segments[index], out var actual) || actual != segments[index])
                    throw new InvalidDataException($"Missing or case-aliased evidence path: {path}");
                current = Path.Combine(current, actual);
                Check(current, index < segments.Length - 1 || directory);
            }
            return full;
        }
        private Dictionary<string, string> Entries(string directory)
        {
            if (entries.TryGetValue(directory, out var cached)) return cached;
            Check(directory, directory: true);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(entry);
                // Validate selected paths; aliases in their parent can never be resolved safely.
                if (!names.TryAdd(name, name)) throw new InvalidDataException($"Case-aliased filesystem entries: {directory}");
            }
            entries.Add(directory, names);
            return names;
        }
        public string[] Tree(string root, string relative)
        {
            var directory = relative.Length == 0 ? root : Directory(root, relative);
            var files = new List<string>();
            Walk(directory);
            if (files.Count == 0) throw new InvalidDataException($"Required tree is empty: {directory}");
            return files.Order(StringComparer.Ordinal).ToArray();
            void Walk(string current)
            {
                foreach (var name in Entries(current).Values)
                {
                    var full = Path.Combine(current, name);
                    var path = SafePath(Collectors.Relative(root, full));
                    var attributes = System.IO.File.GetAttributes(full);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"Linked evidence path: {path}");
                    if ((attributes & FileAttributes.Directory) != 0) Walk(full);
                    else { Check(full, directory: false); files.Add(path); }
                }
            }
        }
        private static void Check(string path, bool directory)
        {
            var attributes = System.IO.File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new InvalidDataException($"Linked or nonregular evidence path: {path}");
            if (((attributes & FileAttributes.Directory) != 0) != directory)
                throw new InvalidDataException($"Expected {(directory ? "directory" : "regular file")}: {path}");
        }
    }
}
