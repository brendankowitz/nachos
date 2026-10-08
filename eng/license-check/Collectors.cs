using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Nachos.LicenseCheck;

internal static class Collectors
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "node_modules", "bin", "obj", "dist", ".astro", "artifacts", "fixtures-work" };
    private static readonly string[] UnsupportedLocks = ["yarn.lock", "uv.lock", "poetry.lock", "Pipfile.lock"];

    public static List<PackageEvidence> Collect(AuditInputs inputs, List<string> errors)
    {
        var packages = new List<PackageEvidence>();
        Capture(() => Nuget(inputs, packages, errors), inputs.NugetInventory, errors);
        var toolingLock = Path.Combine(inputs.Root, ".github", "scripts", "package-lock.json");
        if (!File.Exists(toolingLock))
        {
            errors.Add($"Required current tooling inventory missing: {toolingLock}");
        }
        Capture(() =>
        {
            foreach (var directory in SourceDirectories(inputs.Root))
            {
                foreach (var unsupported in UnsupportedLocks.Where(name => File.Exists(Path.Combine(directory, name))))
                {
                    errors.Add($"Unsupported lock input requires a collector: {Path.Combine(directory, unsupported)}");
                }
                var manifest = Path.Combine(directory, "package.json");
                var npmLock = Path.Combine(directory, "package-lock.json");
                var pnpmLock = Path.Combine(directory, "pnpm-lock.yaml");
                if (File.Exists(manifest) || File.Exists(npmLock) || File.Exists(pnpmLock))
                {
                    Capture(() =>
                    {
                        using var package = JsonDocument.Parse(File.ReadAllText(Under(directory, "package.json")));
                        var manager = package.RootElement.TryGetProperty("packageManager", out var pin) ? pin.GetString() : null;
                        if (File.Exists(npmLock) && File.Exists(pnpmLock))
                            throw new InvalidDataException("Unsupported lock choice: ambiguous npm/pnpm locks; select the declared manager, not a convenient inventory.");
                        if (File.Exists(pnpmLock) || manager?.StartsWith("pnpm@", StringComparison.Ordinal) == true)
                            Pnpm(inputs, pnpmLock, packages, errors);
                        else
                        {
                            if (manager is not null && !manager.StartsWith("npm@", StringComparison.Ordinal))
                                throw new InvalidDataException($"Unsupported packageManager declaration: {manager}");
                            Npm(inputs, npmLock, packages, errors);
                        }
                    }, directory, errors);
                }
                if (File.Exists(Path.Combine(directory, "pyproject.toml")) || File.Exists(Path.Combine(directory, "requirements.txt")))
                {
                    if (!File.Exists(Path.Combine(directory, "requirements.lock")))
                    {
                        errors.Add($"{directory}: Python dependencies require requirements.lock.");
                    }
                }
                foreach (var pythonLock in Directory.EnumerateFiles(directory, "requirements.lock"))
                {
                    Capture(() => Python(inputs, pythonLock, packages, errors), pythonLock, errors);
                }
            }
        }, inputs.Root, errors);
        return packages;
    }

    internal static void Capture(Action action, string context, List<string> errors)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or XmlException or FormatException or ArgumentException or KeyNotFoundException)
        {
            errors.Add($"{context}: {exception.Message}");
        }
    }

    private static IEnumerable<string> SourceDirectories(string root)
    {
        yield return root;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (ExcludedDirectories.Contains(Path.GetFileName(directory)))
            {
                continue;
            }
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Cannot establish provenance through linked directory {directory}.");
            }
            foreach (var nested in SourceDirectories(directory))
            {
                yield return nested;
            }
        }
    }

    private static void Nuget(AuditInputs inputs, List<PackageEvidence> packages, List<string> errors)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(inputs.NugetInventory));
        var root = document.RootElement;
        if (root.GetProperty("version").GetInt32() != 1 || root.TryGetProperty("errors", out _)
            || !RequiredString(root, "parameters").Contains("--include-transitive", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Invalid dotnet list package JSON or restore errors.");
        }
        var projects = root.GetProperty("projects").EnumerateArray().ToArray();
        var solution = ReadXml(File.ReadAllText(Path.Combine(inputs.Root, "Nachos.slnx")));
        var expected = solution.Descendants("Project").Select(project =>
            Path.GetFullPath(Path.Combine(inputs.Root, project.Attribute("Path")!.Value.Replace('/', Path.DirectorySeparatorChar))))
            .ToHashSet(PathComparer);
        var actual = projects.Select(project => Path.GetFullPath(project.GetProperty("path").GetString()!)).ToHashSet(PathComparer);
        if (expected.Count == 0 || !expected.SetEquals(actual))
        {
            throw new InvalidDataException("NuGet inventory must cover every current solution project.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            if (project.TryGetProperty("errors", out _) || !project.TryGetProperty("frameworks", out var frameworks)
                || frameworks.GetArrayLength() == 0)
            {
                throw new InvalidDataException($"Missing restored frameworks for {project.GetProperty("path")}.");
            }
            var projectPackages = new HashSet<string>(StringComparer.Ordinal);
            foreach (var framework in frameworks.EnumerateArray())
            {
                foreach (var category in new[] { "topLevelPackages", "transitivePackages" })
                {
                    if (!framework.TryGetProperty(category, out var dependencies))
                    {
                        continue;
                    }
                    foreach (var dependency in dependencies.EnumerateArray())
                    {
                        var name = RequiredString(dependency, "id");
                        var version = RequiredString(dependency, "resolvedVersion");
                        projectPackages.Add(PackageEvidence.Identity("nuget", name, version));
                        if (!seen.Add(PackageEvidence.Identity("nuget", name, version)))
                        {
                            continue;
                        }
                        Capture(() => packages.Add(ReadNuget(inputs, name, version)), $"{name}@{version}", errors);
                    }
                }
                var assetsPath = Path.Combine(Path.GetDirectoryName(RequiredString(project, "path"))!, "obj", "project.assets.json");
                using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
                var restored = assets.RootElement.GetProperty("libraries").EnumerateObject()
                    .Where(library => RequiredString(library.Value, "type") == "package")
                    .Select(library =>
                    {
                        var slash = library.Name.LastIndexOf('/');
                        if (slash <= 0)
                        {
                            throw new InvalidDataException("Malformed NuGet restore graph package identity.");
                        }
                        return PackageEvidence.Identity("nuget", library.Name[..slash], library.Name[(slash + 1)..]);
                    }).ToHashSet(StringComparer.Ordinal);
                if (!projectPackages.SetEquals(restored))
                {
                    throw new InvalidDataException($"NuGet inventory disagrees with restore graph: {assetsPath}");
                }
            }
        }
    }

    private static PackageEvidence ReadNuget(AuditInputs inputs, string name, string version)
    {
        var lower = name.ToLowerInvariant();
        var archive = Under(inputs.NugetCache, $"{lower}/{version.ToLowerInvariant()}/{lower}.{version.ToLowerInvariant()}.nupkg");
        using var zip = ZipFile.OpenRead(archive);
        var nuspecs = zip.Entries.Where(entry => !entry.FullName.Contains('/', StringComparison.Ordinal)
            && entry.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (nuspecs.Length != 1)
        {
            throw new InvalidDataException("Expected exactly one nupkg nuspec.");
        }
        var metadata = ReadXml(ReadEntry(nuspecs[0])).Descendants().Single(element => element.Name.LocalName == "metadata");
        string? Value(string field) => metadata.Elements().SingleOrDefault(element => element.Name.LocalName == field)?.Value;
        if (!string.Equals(Value("id"), name, StringComparison.OrdinalIgnoreCase) || Value("version") != version)
        {
            throw new InvalidDataException("nupkg identity disagrees with resolved inventory.");
        }
        var license = metadata.Elements().SingleOrDefault(element => element.Name.LocalName == "license");
        var declaredFile = license?.Attribute("type")?.Value == "file" ? license.Value.Replace('\\', '/') : null;
        var texts = zip.Entries.Where(entry => LicenseText.IsLicensePath(entry.FullName) || entry.FullName == declaredFile)
            .Select(entry =>
            {
                if (!LicenseText.IsDocumentationPath(entry.FullName))
                {
                    throw new InvalidDataException($"unsupported license entry (not a documentation file): {entry.FullName}");
                }
                return new LicenseFile(archive + "!" + entry.FullName, ReadEntry(entry),
                    !LicenseText.IsNoticePath(entry.FullName) && (declaredFile is null || entry.FullName == declaredFile));
            }).ToList();
        if (declaredFile is not null && zip.GetEntry(declaredFile) is null)
        {
            texts.Add(new LicenseFile(archive + "!" + declaredFile, ""));
        }
        return new PackageEvidence("nuget", name, version, inputs.NugetInventory,
            license?.Attribute("type")?.Value == "expression" ? license.Value : null, texts, archive);
    }

    private static void Npm(AuditInputs inputs, string path, List<PackageEvidence> packages, List<string> errors)
    {
        var records = NpmLock.Read(path);
        var directory = Path.GetDirectoryName(path)!;
        foreach (var (location, locked) in records)
        {
            if (location.Length == 0)
            {
                continue;
            }
            Capture(() =>
            {
                var installed = Under(directory, location);
                var version = RequiredString(locked, "version");
                var expectedName = NpmLock.Name(location);
                Dictionary<string, string>? archived = null;
                string? archivePath = null;
                var archiveRoot = "package";
                if (!Directory.Exists(installed))
                {
                    archived = NpmArchives.Read(locked, expectedName, inputs.NpmArchives
                        ?? throw new InvalidDataException("Locked npm package is not installed; npm archive directory is required."), out archivePath, out archiveRoot);
                }
                using var metadata = JsonDocument.Parse(archived is null
                    ? File.ReadAllText(Under(installed, "package.json")) : archived["package.json"]);
                var name = RequiredString(metadata.RootElement, "name");
                if (name != expectedName || RequiredString(metadata.RootElement, "version") != version)
                {
                    throw new InvalidDataException("npm package identity disagrees with package-lock.json.");
                }
                NpmLock.CompareDeclarations(metadata.RootElement, locked);
                var installedLicense = NpmLicense(metadata.RootElement);
                var lockedLicense = NpmLicense(locked);
                if (lockedLicense is not null && installedLicense != lockedLicense)
                {
                    throw new InvalidDataException("npm installed license metadata disagrees with package-lock.json.");
                }
                packages.Add(NpmEvidence(inputs, path, installed, locked, metadata.RootElement, archived, archivePath, archiveRoot));
            }, $"{path}:{location}", errors);
        }
    }

    private static void Pnpm(AuditInputs inputs, string path, List<PackageEvidence> packages, List<string> errors)
    {
        var graph = PnpmLock.Read(path);
        var cache = inputs.NpmArchives ?? throw new InvalidDataException("pnpm requires verified archives for every locked package, including installed packages.");
        var installed = PnpmInstalled.Read(Path.GetDirectoryName(path)!, graph);
        var verified = new Dictionary<PnpmPackage, (Dictionary<string, string> Evidence, string Archive, string Root, JsonElement Metadata)>();
        foreach (var package in graph.Packages)
        {
            Capture(() =>
            {
                var evidence = NpmArchives.Read(package.Download, package.Name, cache, out var archive, out var archiveRoot);
                using var metadata = JsonDocument.Parse(evidence["package.json"]);
                PnpmLock.ValidateArchiveMetadata(package, metadata.RootElement);
                verified.Add(package, (evidence, archive, archiveRoot, metadata.RootElement.Clone()));
            }, $"{path}:document={package.Document}:{package.Name}@{package.Version}", errors);
        }
        // Cross-node requirements use archive declarations, independent of lock enumeration order.
        var declarations = verified.ToDictionary(entry => entry.Key, entry => entry.Value.Metadata);
        foreach (var (package, archive) in verified)
        {
            Capture(() =>
            {
                graph.ValidateMetadata(package, declarations);
                var decision = NpmEvidence(inputs, path, Path.Combine(Path.GetDirectoryName(path)!, "node_modules", package.Name),
                    package.Download, archive.Metadata, archive.Evidence, archive.Archive, archive.Root);
                foreach (var location in installed.GetValueOrDefault(package.Name + "@" + package.Version, []))
                    PnpmInstalled.Verify(location, package, cache, archive.Evidence);
                packages.Add(decision with { Origin = Relative(inputs.Root, path) + $"#document={package.Document}" });
            }, $"{path}:document={package.Document}:{package.Name}@{package.Version}", errors);
        }
    }

    private static PackageEvidence NpmEvidence(AuditInputs inputs, string path, string installed, JsonElement locked,
        JsonElement metadata, Dictionary<string, string>? archived, string? archivePath, string archiveRoot)
    {
        var name = RequiredString(metadata, "name");
        var version = RequiredString(locked, "version");
        var installedLicense = NpmLicense(metadata);
        var declared = installedLicense?.StartsWith("SEE LICENSE IN ", StringComparison.Ordinal) == true
            ? installedLicense["SEE LICENSE IN ".Length..] : null;
        if (declared is not null)
        {
            Under(installed, declared);
            if (declared.Contains('\\', StringComparison.Ordinal) || declared.Split('/').Any(part => part is "." or ".." or "")
                || !LicenseText.IsDocumentationPath(declared))
            {
                throw new InvalidDataException($"unsupported license entry (not a documentation file): {declared}");
            }
        }
        if (archived is not null && declared is not null && !archived.ContainsKey(declared))
        {
            archived = NpmArchives.Read(locked, name, inputs.NpmArchives!, out archivePath, out archiveRoot, declared);
        }
        var evidence = archived ?? InstalledNpmEvidence(installed);
        if (declared is not null && !evidence.ContainsKey(declared))
        {
            var declaredPath = Under(installed, declared);
            evidence.Add(declared, archived is null && File.Exists(declaredPath) ? File.ReadAllText(declaredPath) : "");
        }
        var texts = evidence.Where(item => item.Key != "package.json").Select(item =>
        {
            var primary = !LicenseText.IsNoticePath(item.Key) && !VendoredPath(item.Key)
                && (declared is null ? !item.Key.Contains('/') || item.Key.StartsWith("licenses/", StringComparison.OrdinalIgnoreCase)
                    || item.Key.StartsWith("licences/", StringComparison.OrdinalIgnoreCase) : item.Key == declared);
            return new LicenseFile((archivePath is null ? Relative(inputs.Root, installed) + "/"
                : Relative(inputs.Root, archivePath) + "!" + archiveRoot + "/") + item.Key, item.Value, primary);
        }).ToArray();
        return new PackageEvidence("npm", name, version, Relative(inputs.Root, path), declared is null ? installedLicense : null, texts);
    }

    internal static Dictionary<string, string> InstalledNpmEvidence(string installed, bool includeNestedDirectories = false)
    {
        var evidence = new Dictionary<string, string>(StringComparer.Ordinal);
        var directories = new Stack<string>();
        directories.Push(installed);
        while (directories.TryPop(out var directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var relative = Relative(installed, file);
                if (LicenseText.IsImplicitNpmDocument(relative))
                {
                    evidence.Add(relative, File.ReadAllText(Under(installed, relative)));
                }
            }
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (includeNestedDirectories || directory != installed || Path.GetFileName(child).Equals("licenses", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(child).Equals("licences", StringComparison.OrdinalIgnoreCase))
                {
                    directories.Push(Under(installed, Relative(installed, child)));
                }
            }
        }
        return evidence;
    }

    internal static string? NpmLicense(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("license", out var license))
        {
            return null;
        }
        return license.ValueKind == JsonValueKind.String ? license.GetString() : RequiredString(license, "type");
    }

    private static void Python(AuditInputs inputs, string path, List<PackageEvidence> packages, List<string> errors)
    {
        if (inputs.PythonArchives is null || !Directory.Exists(inputs.PythonArchives))
        {
            throw new InvalidDataException("Python lock exists; downloaded wheels/sdists directory is required.");
        }
        var lockText = Regex.Replace(File.ReadAllText(path), @"\\\r?\n", " ");
        var requirements = new Dictionary<string, (string Name, string Version, string[] Hashes)>(StringComparer.Ordinal);
        foreach (var raw in lockText.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }
            var match = Regex.Match(line, @"^([A-Za-z0-9][A-Za-z0-9._-]*)==([A-Za-z0-9][A-Za-z0-9.!+_-]*)(\s+--hash=sha256:[a-fA-F0-9]{64})*$");
            if (!match.Success)
            {
                throw new InvalidDataException($"Unsupported or unpinned Python requirement: {line}");
            }
            var name = match.Groups[1].Value;
            var version = match.Groups[2].Value;
            requirements.Add(PackageEvidence.Identity("python", name, version),
                (name, version, Regex.Matches(line, @"--hash=sha256:([a-fA-F0-9]{64})").Select(hash => hash.Groups[1].Value).ToArray()));
        }
        if (requirements.Count == 0)
        {
            throw new InvalidDataException("Python requirements.lock is empty.");
        }
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var archive in Directory.EnumerateFiles(inputs.PythonArchives))
        {
            if (!archive.EndsWith(".whl", StringComparison.OrdinalIgnoreCase)
                && !archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && !archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Unsupported Python archive: {archive}");
            }
            Capture(() =>
            {
                var entries = PythonEntries(archive, []);
                var wheel = archive.EndsWith(".whl", StringComparison.OrdinalIgnoreCase);
                var metadataEntries = entries.Where(entry => wheel
                    ? entry.Key.EndsWith(".dist-info/METADATA", StringComparison.Ordinal)
                    : entry.Key.EndsWith("/PKG-INFO", StringComparison.Ordinal)).ToArray();
                if (metadataEntries.Length == 0)
                {
                    throw new InvalidDataException("Python archive has no package metadata.");
                }
                if (wheel && (metadataEntries.Length != 1 || metadataEntries[0].Key.Count(character => character == '/') != 1))
                {
                    throw new InvalidDataException("Wheel requires exactly one authoritative top-level .dist-info/METADATA directory.");
                }
                var metadata = metadataEntries.OrderBy(entry => entry.Key.Count(character => character == '/')).First();
                var headers = Headers(metadata.Value);
                var expression = PythonLicense(headers);
                var name = headers["Name"].Single();
                var version = headers["Version"].Single();
                var key = PackageEvidence.Identity("python", name, version);
                var licenseFiles = headers.GetValueOrDefault("License-File", []).Order(StringComparer.Ordinal).ToArray();
                foreach (var candidate in metadataEntries)
                {
                    var other = Headers(candidate.Value);
                    if (PackageEvidence.Identity("python", other["Name"].Single(), other["Version"].Single()) != key
                        || PythonLicense(other) != expression
                        || !other.GetValueOrDefault("License-File", []).Order(StringComparer.Ordinal).SequenceEqual(licenseFiles))
                    {
                        throw new InvalidDataException("Conflicting Python archive metadata identities or licensing declarations.");
                    }
                }
                var declaredFiles = new HashSet<string>(StringComparer.Ordinal);
                var packageRoot = wheel ? metadata.Key[..metadata.Key.LastIndexOf('/')] : metadata.Key.Split('/')[0];
                if (headers.TryGetValue("License-File", out var declared))
                {
                    var parent = metadata.Key[..metadata.Key.LastIndexOf('/')];
                    foreach (var licenseFile in declared)
                    {
                        if (licenseFile.StartsWith('/') || licenseFile.Contains('\\', StringComparison.Ordinal)
                            || licenseFile.Split('/').Any(segment => segment is ".." or "." or "")
                            || !LicenseText.IsDocumentationPath(licenseFile))
                        {
                            throw new InvalidDataException($"Invalid Python License-File: {licenseFile}");
                        }
                        declaredFiles.Add(parent + "/" + licenseFile);
                        if (metadata.Key.EndsWith(".dist-info/METADATA", StringComparison.Ordinal))
                        {
                            declaredFiles.Add(parent + "/licenses/" + licenseFile);
                        }
                    }
                    entries = PythonEntries(archive, declaredFiles);
                    foreach (var licenseFile in declared)
                    {
                        if (!entries.ContainsKey(parent + "/" + licenseFile) && !entries.ContainsKey(parent + "/licenses/" + licenseFile))
                        {
                            entries.Add(parent + "/License-File missing: " + licenseFile, "");
                            declaredFiles.Add(parent + "/License-File missing: " + licenseFile);
                        }
                    }
                }
                if (!requirements.TryGetValue(key, out var requirement))
                {
                    throw new InvalidDataException($"Downloaded Python archive is not in requirements.lock: {name}@{version}");
                }
                if (requirement.Hashes.Length > 0)
                {
                    using var stream = File.OpenRead(archive);
                    var hash = Convert.ToHexString(SHA256.HashData(stream));
                    if (!requirement.Hashes.Contains(hash, StringComparer.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Python archive SHA256 does not match requirements.lock.");
                    }
                }
                var texts = entries.Where(entry => LicenseText.IsLicensePath(entry.Key) || declaredFiles.Contains(entry.Key)
                    || entry.Key.Contains(".dist-info/licenses/", StringComparison.Ordinal))
                    .Select(entry =>
                    {
                        var relative = entry.Key.StartsWith(packageRoot + "/", StringComparison.Ordinal)
                            ? entry.Key[(packageRoot.Length + 1)..] : null;
                        var established = relative is not null && (!relative.Contains('/', StringComparison.Ordinal)
                            || relative.StartsWith("licenses/", StringComparison.Ordinal) && relative.Count(character => character == '/') == 1);
                        var primary = !LicenseText.IsNoticePath(entry.Key) && !VendoredPath(entry.Key)
                            && (established || declaredFiles.Contains(entry.Key));
                        return new LicenseFile(archive + "!" + entry.Key, entry.Value, primary);
                    }).ToArray();
                packages.Add(new PackageEvidence("python", name, version, Relative(inputs.Root, path), expression, texts));
                found.Add(key);
            }, archive, errors);
        }

        foreach (var missing in requirements.Keys.Except(found))
        {
            errors.Add($"{path}: required Python archive missing for {missing}.");
        }
    }

    private static bool VendoredPath(string path) => path.Split('/').Any(segment =>
        segment.Equals("_vendor", StringComparison.OrdinalIgnoreCase) || segment.Equals("vendor", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("vendored", StringComparison.OrdinalIgnoreCase) || segment.Equals("third_party", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("third-party", StringComparison.OrdinalIgnoreCase) || segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase));

    private static string? PythonLicense(Dictionary<string, List<string>> headers)
    {
        var declarations = new List<string>();
        foreach (var field in new[] { "License-Expression", "License" })
        {
            if (headers.TryGetValue(field, out var values))
            {
                declarations.Add(values.Single());
            }
        }
        if (headers.TryGetValue("Classifier", out var classifiers))
        {
            foreach (var classifier in classifiers.Where(value => value.StartsWith("License ::", StringComparison.Ordinal)))
            {
                declarations.Add(classifier switch
                {
                    "License :: OSI Approved :: MIT License" => "MIT",
                    "License :: OSI Approved :: Apache Software License" => "Apache-2.0",
                    "License :: OSI Approved :: ISC License (ISCL)" => "ISC",
                    "License :: OSI Approved :: Python Software Foundation License" => "PSF-2.0",
                    "License :: OSI Approved :: Mozilla Public License 2.0 (MPL 2.0)" => "MPL-2.0",
                    _ => classifier
                });
            }
        }
        if (declarations.Count == 0)
        {
            return null;
        }
        if (declarations.Any(LicenseText.Prohibited))
        {
            throw new InvalidDataException("prohibited Python licensing declaration: " + string.Join("; ", declarations));
        }
        string Canonical(string value) => string.Join(" OR ", Spdx.Parse(value).Alternatives
            .Select(branch => string.Join(" AND ", branch.Order(StringComparer.Ordinal))).Order(StringComparer.Ordinal));
        var canonical = declarations.Select(Canonical).Distinct(StringComparer.Ordinal).ToArray();
        if (canonical.Length != 1)
        {
            throw new InvalidDataException("Conflicting Python licensing declarations: " + string.Join("; ", declarations));
        }
        return canonical[0];
    }

    private static Dictionary<string, List<string>> Headers(string text)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? previous = null;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0)
            {
                break;
            }
            if (char.IsWhiteSpace(line[0]) && previous is not null)
            {
                result[previous][^1] += " " + line.Trim();
                continue;
            }
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw new InvalidDataException("Malformed Python package metadata header.");
            }
            previous = line[..colon];
            if (!result.TryGetValue(previous, out var values))
            {
                values = [];
                result.Add(previous, values);
            }
            values.Add(line[(colon + 1)..].Trim());
        }
        return result;
    }

    private static Dictionary<string, string> PythonEntries(string archive, HashSet<string> declaredFiles)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        bool EvidenceOnly(string name)
        {
            if (name.EndsWith(".dist-info/METADATA", StringComparison.Ordinal) || name.EndsWith("/PKG-INFO", StringComparison.Ordinal))
            {
                return true;
            }
            if (name.Contains(".dist-info/licenses/", StringComparison.Ordinal) && !name.EndsWith('/') || declaredFiles.Contains(name)
                || LicenseText.IsLicensePath(name))
            {
                if (!LicenseText.IsDocumentationPath(name))
                {
                    throw new InvalidDataException($"unsupported license entry (not a documentation file): {name}");
                }
                return true;
            }
            return false;
        }
        if (archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(archive);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            while (tar.GetNextEntry() is { } entry)
            {
                if (EvidenceOnly(entry.Name))
                {
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
                    {
                        throw new InvalidDataException($"Linked or invalid Python evidence entry: {entry.Name}");
                    }
                    using var reader = new StreamReader(entry.DataStream);
                    entries.Add(entry.Name, reader.ReadToEnd());
                }
            }
        }
        else
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries.Where(entry => EvidenceOnly(entry.FullName)))
            {
                entries.Add(entry.FullName, ReadEntry(entry));
            }
        }
        return entries;
    }

    private static XDocument ReadXml(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        return XDocument.Load(reader);
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    internal static string RequiredString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new InvalidDataException($"Required nonempty string: {property}");

    internal static string Under(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Path escapes evidence root: {relative}");
        }
        for (var current = full; current.Length >= prefix.Length; current = Path.GetDirectoryName(current)!)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Linked evidence path is not supported: {relative}");
            }
        }
        return full;
    }

    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
