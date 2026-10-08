using System.Text.Json;

namespace Nachos.LicenseCheck;

internal static class PnpmInstalled
{
    public static Dictionary<string, List<string>> Read(string directory, PnpmLock graph)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var store = Collectors.Under(directory, "node_modules/.pnpm");
        if (!Directory.Exists(store)) return result;
        var known = graph.Packages.Select(package => package.Name + "@" + package.Version).ToHashSet(StringComparer.Ordinal);
        foreach (var context in Directory.EnumerateDirectories(store))
        {
            if (Path.GetFileName(context) == "node_modules") continue;
            var container = Collectors.Under(store, Collectors.Relative(store, context) + "/node_modules");
            if (!Directory.Exists(container)) throw new InvalidDataException($"Unsupported pnpm store layout: {context}");
            foreach (var entry in Directory.EnumerateDirectories(container))
            {
                if (Path.GetFileName(entry) == ".bin") continue;
                if (Path.GetFileName(entry).StartsWith('@'))
                {
                    Collectors.Under(store, Collectors.Relative(store, entry));
                    foreach (var scoped in Directory.EnumerateDirectories(entry)) Inspect(scoped);
                }
                else Inspect(entry);
            }
        }
        return result;

        void Inspect(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                var target = Directory.ResolveLinkTarget(path, returnFinalTarget: true)
                    ?? throw new InvalidDataException($"Unresolved pnpm dependency link: {path}");
                if (!Directory.Exists(Collectors.Under(store, Collectors.Relative(store, target.FullName))))
                    throw new InvalidDataException($"Missing pnpm dependency link target: {path}");
                return;
            }
            var file = Collectors.Under(store, Collectors.Relative(store, path) + "/package.json");
            using var metadata = JsonDocument.Parse(File.ReadAllText(file));
            var key = Collectors.RequiredString(metadata.RootElement, "name") + "@" + Collectors.RequiredString(metadata.RootElement, "version");
            if (!known.Contains(key)) throw new InvalidDataException($"Installed pnpm package absent from lock: {key}");
            if (!result.TryGetValue(key, out var paths)) result.Add(key, paths = []);
            paths.Add(path);
        }
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
