using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nachos.LicenseCheck;

internal static class NpmLock
{
    private static readonly string[] DependencyFields = ["dependencies", "optionalDependencies", "peerDependencies"];

    public static Dictionary<string, JsonElement> Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.GetProperty("lockfileVersion").GetInt32() is not (2 or 3))
        {
            throw new InvalidDataException("Only npm lockfileVersion 2 and 3 are supported.");
        }
        var records = document.RootElement.GetProperty("packages").EnumerateObject()
            .ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
        if (!records.TryGetValue("", out var root))
        {
            throw new InvalidDataException("Incomplete npm lock: packages must contain the root record.");
        }
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "package.json")));
        CompareDeclarations(manifest.RootElement, root, root: true);
        foreach (var field in new[] { "name", "version" })
        {
            if (manifest.RootElement.TryGetProperty(field, out var value)
                && (!root.TryGetProperty(field, out var locked) || value.GetString() != locked.GetString()))
            {
                throw new InvalidDataException($"npm root manifest and lock disagree on {field}.");
            }
        }
        foreach (var (location, record) in records)
        {
            if (location.Length > 0)
            {
                if (!Regex.IsMatch(location, @"^node_modules/(?:@[a-z0-9._-]+/)?[a-z0-9][a-z0-9._-]*(?:/node_modules/(?:@[a-z0-9._-]+/)?[a-z0-9][a-z0-9._-]*)*$")
                    || record.TryGetProperty("link", out var link) && link.GetBoolean())
                {
                    throw new InvalidDataException($"Unsupported or unsafe npm package location: {location}");
                }
                Collectors.RequiredString(record, "version");
                if (!records.ContainsKey(Parent(location)))
                {
                    throw new InvalidDataException($"Incomplete npm lock: missing parent for {location}.");
                }
            }
        }
        var overrides = NpmOverrides.Read(manifest.RootElement);
        var visited = new HashSet<(string Location, NpmOverrides Context)>();
        var reached = new HashSet<string>(StringComparer.Ordinal);
        ValidateEdges("");
        foreach (var location in records.Keys.Where(location => !reached.Contains(location)))
        {
            ValidateEdges(location);
        }
        return records;

        void ValidateEdges(string start)
        {
            var pending = new Queue<(string Location, NpmOverrides Context)>();
            pending.Enqueue((start, overrides));
            while (pending.TryDequeue(out var current))
            {
                if (!visited.Add(current))
                {
                    continue;
                }
                var (location, context) = current;
                reached.Add(location);
                var record = records[location];
                var optional = Map(record, "optionalDependencies");
                foreach (var field in DependencyFields.Concat(location.Length == 0 ? ["devDependencies"] : Array.Empty<string>()))
                {
                    foreach (var (name, request) in Map(record, field))
                    {
                        if (!Regex.IsMatch(name, @"^(?:@[a-z0-9._-]+/)?[a-z0-9][a-z0-9._-]*$"))
                        {
                            throw new InvalidDataException($"Invalid npm dependency name: {name}");
                        }
                        if (field == "dependencies" && optional.ContainsKey(name))
                        {
                            continue;
                        }
                        var target = Resolve(records, location, name, peer: field == "peerDependencies");
                        var optionalPeer = field == "peerDependencies" && record.TryGetProperty("peerDependenciesMeta", out var peerMeta)
                            && peerMeta.TryGetProperty(name, out var peer) && peer.TryGetProperty("optional", out var flag) && flag.GetBoolean();
                        if (target is null)
                        {
                            if (optional.ContainsKey(name) || optionalPeer)
                            {
                                continue;
                            }
                            throw new InvalidDataException($"Incomplete npm lock: {location} requires missing {field} record {name}.");
                        }
                        var (childContext, effective) = context.ForDependency(name, request);
                        if (location.Length == 0 && effective != request)
                        {
                            throw new InvalidDataException($"Unsupported npm override of direct dependency {name}: {request} -> {effective}.");
                        }
                        if (NpmOverrides.IsExactVersion(effective) && Collectors.RequiredString(records[target], "version") != effective)
                        {
                            throw new InvalidDataException($"npm lock version disagrees with exact dependency {name}@{effective} from {location}.");
                        }
                        pending.Enqueue((target, childContext));
                    }
                }
            }
        }
    }

    public static string Name(string location) => location[(location.LastIndexOf("node_modules/", StringComparison.Ordinal) + "node_modules/".Length)..];

    public static void CompareDeclarations(JsonElement metadata, JsonElement locked, bool root = false)
    {
        foreach (var field in DependencyFields.Concat(root ? ["devDependencies"] : Array.Empty<string>()))
        {
            if (!Map(metadata, field).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SequenceEqual(Map(locked, field).OrderBy(pair => pair.Key, StringComparer.Ordinal)))
            {
                throw new InvalidDataException($"npm manifest dependencies disagree with lock: {field}.");
            }
        }
        var left = OptionalPeers(metadata);
        var right = OptionalPeers(locked);
        if (!left.SetEquals(right))
        {
            throw new InvalidDataException("npm optional peer metadata disagrees with lock.");
        }
    }

    private static Dictionary<string, string> Map(JsonElement record, string field)
    {
        if (!record.TryGetProperty(field, out var values))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        return values.EnumerateObject().ToDictionary(pair => pair.Name,
            pair => pair.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pair.Value.GetString())
                ? pair.Value.GetString()! : throw new InvalidDataException($"Invalid npm dependency declaration: {field}/{pair.Name}"),
            StringComparer.Ordinal);
    }

    private static HashSet<string> OptionalPeers(JsonElement record) => record.TryGetProperty("peerDependenciesMeta", out var metadata)
        ? metadata.EnumerateObject().Where(pair => pair.Value.TryGetProperty("optional", out var optional) && optional.GetBoolean())
            .Select(pair => pair.Name).ToHashSet(StringComparer.Ordinal)
        : new HashSet<string>(StringComparer.Ordinal);

    private static string Parent(string location)
    {
        var separator = location.LastIndexOf("/node_modules/", StringComparison.Ordinal);
        return separator < 0 ? "" : location[..separator];
    }

    private static string? Resolve(Dictionary<string, JsonElement> records, string location, string name, bool peer)
    {
        var current = peer ? Parent(location) : location;
        while (true)
        {
            var path = (current.Length == 0 ? "" : current + "/") + "node_modules/" + name;
            if (records.ContainsKey(path))
            {
                return path;
            }
            if (current.Length == 0)
            {
                return null;
            }
            current = Parent(current);
        }
    }
}
