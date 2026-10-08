using System.Text.Json;

namespace Nachos.LicenseCheck;

internal static class PnpmInstalled
{
    public static Dictionary<string, List<string>> Read(string directory, PnpmLock graph)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var store = Collectors.Under(directory, "node_modules/.pnpm");
        if (!Directory.Exists(store)) return result;
        var physical = new Dictionary<string, (string Name, string Context)>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var links = new List<(string Path, string Slot, string? Expected)>();
        foreach (var context in Directory.EnumerateDirectories(store))
        {
            var shared = Path.GetFileName(context) == "node_modules";
            var container = Collectors.Under(store, Collectors.Relative(store, context) + (shared ? "" : "/node_modules"));
            if (!Directory.Exists(container)) throw new InvalidDataException($"Unsupported pnpm store layout: {context}");
            var owner = shared ? ((PnpmPackage Package, string Key)?)null : graph.StoreContext(Path.GetFileName(context));
            var bindings = owner is { } resolved ? PnpmLock.Strings(resolved.Package.Snapshots[resolved.Key], "dependencies")
                : new Dictionary<string, string>(StringComparer.Ordinal);
            if (owner is { } current)
                foreach (var (name, reference) in PnpmLock.Strings(current.Package.Snapshots[current.Key], "optionalDependencies"))
                    if (!bindings.TryAdd(name, reference)) throw new InvalidDataException($"Duplicate installed pnpm binding: {current.Key}/{name}");
            foreach (var entry in Directory.EnumerateDirectories(container))
            {
                if (Path.GetFileName(entry) == ".bin") continue;
                if (Path.GetFileName(entry).StartsWith('@'))
                {
                    Collectors.Under(store, Collectors.Relative(store, entry));
                    foreach (var scoped in Directory.EnumerateDirectories(entry))
                        Inspect(scoped, Path.GetFileName(entry) + "/" + Path.GetFileName(scoped));
                }
                else Inspect(entry, Path.GetFileName(entry));
            }

            void Inspect(string path, string slot)
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    string? expected = null;
                    if (owner is { } source)
                    {
                        expected = slot == source.Package.Name ? source.Key
                            : bindings.TryGetValue(slot, out var reference) ? slot + "@" + reference
                            : throw new InvalidDataException($"Undeclared installed pnpm dependency: {source.Key}/{slot}");
                    }
                    links.Add((path, slot, expected));
                    return;
                }
                if (owner is not { } bound || slot != bound.Package.Name)
                    throw new InvalidDataException($"Unbound physical pnpm package slot: {path}");
                using var metadata = JsonDocument.Parse(File.ReadAllText(Collectors.Under(store, Collectors.Relative(store, path) + "/package.json")));
                if (Collectors.RequiredString(metadata.RootElement, "name") != bound.Package.Name
                    || Collectors.RequiredString(metadata.RootElement, "version") != bound.Package.Version)
                    throw new InvalidDataException($"Physical pnpm slot identity disagrees with locked context: {path}");
                physical.Add(Path.GetFullPath(path), (slot, bound.Key));
                var key = bound.Package.Name + "@" + bound.Package.Version;
                if (!result.TryGetValue(key, out var paths)) result.Add(key, paths = []);
                paths.Add(path);
            }
        }
        foreach (var link in links)
        {
            var target = Directory.ResolveLinkTarget(link.Path, returnFinalTarget: true)
                ?? throw new InvalidDataException($"Unresolved pnpm dependency link: {link.Path}");
            var path = Collectors.Under(store, Collectors.Relative(store, target.FullName));
            if (!Directory.Exists(path)) throw new InvalidDataException($"Missing pnpm dependency link target: {link.Path}");
            if (!physical.TryGetValue(path, out var bound) || bound.Name != link.Slot
                || link.Expected is { } expected && bound.Context != expected)
                throw new InvalidDataException($"Installed pnpm link lacks matching physical identity/context evidence: {link.Path}");
        }
        return result;
    }

    public static void Verify(string location, PnpmPackage package, string cache, Dictionary<string, string> archiveEvidence)
    {
        using var physical = JsonDocument.Parse(File.ReadAllText(Collectors.Under(location, "package.json")));
        using var archived = JsonDocument.Parse(archiveEvidence["package.json"]);
        if (!JsonElement.DeepEquals(physical.RootElement, archived.RootElement))
            throw new InvalidDataException($"Installed pnpm metadata differs from integrity-verified archive: {location}");
        var license = Collectors.NpmLicense(archived.RootElement);
        if (license?.StartsWith("SEE LICENSE IN ", StringComparison.Ordinal) == true)
            archiveEvidence = NpmArchives.Read(package.Download, package.Name, cache, out _, license["SEE LICENSE IN ".Length..]);
        var installed = Collectors.InstalledNpmEvidence(location, includeNestedDirectories: true);
        foreach (var (relative, text) in archiveEvidence.Where(item => item.Key != "package.json"))
        {
            if (File.ReadAllText(Collectors.Under(location, relative)) != text)
                throw new InvalidDataException($"Installed pnpm license differs from integrity-verified archive: {location}/{relative}");
        }
        if (installed.Keys.Any(relative => !archiveEvidence.ContainsKey(relative)))
            throw new InvalidDataException($"Installed pnpm package contains additional unverified license evidence: {location}");
    }
}
