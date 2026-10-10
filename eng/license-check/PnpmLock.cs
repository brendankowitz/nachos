using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

namespace Nachos.LicenseCheck;

internal sealed record PnpmPackage(int Document, string Name, string Version, JsonElement Download,
    JsonElement Metadata, Dictionary<string, JsonElement> Snapshots);

internal sealed class PnpmLock
{
    private static readonly string[] Edges = ["dependencies", "optionalDependencies"];
    private static readonly string[] RootEdges = ["dependencies", "devDependencies", "optionalDependencies"];
    private static readonly string[] PlatformFields = ["os", "cpu", "libc"];
    private readonly Dictionary<string, string> overrides;
    public IReadOnlyList<PnpmPackage> Packages { get; }

    private PnpmLock(List<PnpmPackage> packages, Dictionary<string, string> overrides)
    {
        Packages = packages;
        this.overrides = overrides;
    }

    public static PnpmLock Read(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Collectors.Under(directory, Path.GetFileName(path));
            if (Path.GetFileName(path) != "pnpm-lock.yaml" || File.Exists(Path.Combine(directory, "package-lock.json")))
                throw new InvalidDataException("Expected unambiguous pnpm-lock.yaml without package-lock.json.");
            using var manifestFile = JsonDocument.Parse(File.ReadAllText(Collectors.Under(directory, "package.json")));
            var manifest = manifestFile.RootElement;
            var manager = Collectors.RequiredString(manifest, "packageManager");
            if (!Regex.IsMatch(manager, @"^pnpm@12\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$"))
                throw new InvalidDataException("Supported pnpm format requires an exact pnpm@12.x.y packageManager pin.");
            if (manifest.TryGetProperty("pnpm", out _) || manifest.TryGetProperty("overrides", out _)
                || manifest.TryGetProperty("workspaces", out _))
                throw new InvalidDataException("pnpm manifest settings/workspaces are unsupported; use the supported single-importer workspace contract.");
            var rc = Path.Combine(directory, ".npmrc");
            if (File.Exists(rc))
            {
                foreach (var line in File.ReadAllLines(Collectors.Under(directory, ".npmrc")).Select(line => line.Trim())
                    .Where(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith(';')))
                    if (line is not ("registry=https://registry.npmjs.org/" or "registry=https://registry.npmjs.org" or "save-exact=true" or "engine-strict=true"))
                        throw new InvalidDataException($"Unsupported pnpm registry/configuration directive: {line.Split('=')[0]}");
            }
            var workspace = PnpmYaml.Read(Collectors.Under(directory, "pnpm-workspace.yaml"));
            if (workspace.Length != 1) throw new InvalidDataException("pnpm workspace requires one document.");
            Keys(workspace[0], "overrides", "minimumReleaseAge", "minimumReleaseAgeStrict");
            var overrides = Strings(workspace[0], "overrides");
            foreach (var (selector, value) in overrides)
            {
                Selector(selector);
                if (value != "-") Exact(value);
            }
            var documents = PnpmYaml.Read(path);
            if (documents.Length != 2) throw new InvalidDataException("pnpm requires BOTH documents, manager then application.");
            var packages = new List<PnpmPackage>();
            var prior = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            for (var index = 0; index < documents.Length; index++)
            {
                var document = documents[index];
                Keys(document, index == 0 ? ["lockfileVersion", "importers", "packages", "snapshots"]
                    : ["lockfileVersion", "settings", "overrides", "importers", "packages", "snapshots"]);
                if (Collectors.RequiredString(document, "lockfileVersion") != "9.0")
                    throw new InvalidDataException($"Unsupported pnpm lockfileVersion in document {index}.");
                if (index == 1)
                {
                    var settings = Required(document, "settings");
                    Keys(settings, "autoInstallPeers", "excludeLinksFromLockfile");
                    if (Collectors.RequiredString(settings, "autoInstallPeers") != "true"
                        || Collectors.RequiredString(settings, "excludeLinksFromLockfile") != "false")
                        throw new InvalidDataException("Unsupported pnpm peer/link settings.");
                    Same(overrides, Strings(document, "overrides"), "workspace overrides");
                }
                var importers = Map(document, "importers", required: true);
                if (importers.Count != 1 || !importers.TryGetValue(".", out var importer))
                    throw new InvalidDataException($"Unsupported pnpm importer layout in document {index}.");
                var definitions = Map(document, "packages", required: true);
                var snapshots = Map(document, "snapshots", required: true);
                if (definitions.Count == 0 || snapshots.Count == 0) throw new InvalidDataException($"Empty pnpm graph document {index}.");
                foreach (var (key, metadata) in definitions)
                {
                    var (name, version, peers) = Identity(key);
                    if (peers.Count != 0) throw new InvalidDataException($"Context belongs in snapshots, not packages: {key}");
                    Keys(metadata, "resolution", "engines", "cpu", "os", "libc", "hasBin", "deprecated", "peerDependencies", "peerDependenciesMeta");
                    foreach (var field in PlatformFields) Sequence(metadata, field);
                    Strings(metadata, "engines");
                    foreach (var peer in Strings(metadata, "peerDependencies").Keys) Name(peer);
                    OptionalPeers(metadata, yaml: true);
                    if (metadata.TryGetProperty("hasBin", out var hasBin) && Text(hasBin) is not ("true" or "false"))
                        throw new InvalidDataException($"Invalid pnpm hasBin flag: {key}");
                    if (metadata.TryGetProperty("deprecated", out var deprecated)) Text(deprecated);
                    var resolution = Required(metadata, "resolution");
                    Keys(resolution, "integrity");
                    var download = JsonSerializer.SerializeToElement(new
                    {
                        version, integrity = Collectors.RequiredString(resolution, "integrity"),
                        resolved = $"https://registry.npmjs.org/{name}/-/{name.Split('/')[^1]}-{version}.tgz"
                    });
                    NpmArchives.Descriptor(download, name);
                    if (prior.TryGetValue(key, out var other) && !JsonElement.DeepEquals(other, metadata))
                        throw new InvalidDataException($"Conflicting pnpm identity/evidence across documents: {key}");
                    prior[key] = metadata;
                    var contexts = snapshots.Where(pair => BaseIdentity(pair.Key) == key)
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    if (contexts.Count == 0) throw new InvalidDataException($"Missing pnpm snapshot for {key} in document {index}.");
                    packages.Add(new PnpmPackage(index, name, version, download, metadata, contexts));
                }
                foreach (var (key, snapshot) in snapshots)
                {
                    var (name, version, peers) = Identity(key);
                    if (!definitions.ContainsKey(name + "@" + version))
                        throw new InvalidDataException($"pnpm snapshot lacks package evidence: {key}");
                    Keys(snapshot, "dependencies", "optionalDependencies", "transitivePeerDependencies", "optional");
                    foreach (var peer in peers)
                        if (!snapshots.ContainsKey(peer)) throw new InvalidDataException($"Missing pnpm peer context {peer} from {key}.");
                    foreach (var field in Edges)
                        foreach (var (dependency, reference) in Strings(snapshot, field))
                        {
                            Name(dependency);
                            Identity(dependency + "@" + reference);
                            if (!snapshots.ContainsKey(dependency + "@" + reference))
                                throw new InvalidDataException($"Missing pnpm {field} snapshot {dependency}@{reference} from {key}.");
                        }
                    if (snapshot.TryGetProperty("optional", out var optional) && Text(optional) != "true")
                        throw new InvalidDataException($"Invalid pnpm optional flag: {key}");
                    foreach (var peer in Sequence(snapshot, "transitivePeerDependencies")) Name(peer);
                }
                if (index == 0)
                {
                    Keys(importer, "configDependencies", "packageManagerDependencies");
                    if (Map(importer, "configDependencies", required: true).Count != 0)
                        throw new InvalidDataException("pnpm configDependencies are unsupported.");
                    var managers = Map(importer, "packageManagerDependencies", required: true);
                    if (managers.Count != 1 || !managers.TryGetValue("pnpm", out var pinned))
                        throw new InvalidDataException("Missing or extra pnpm manager declaration.");
                    Import("pnpm", manager["pnpm@".Length..], pinned, snapshots);
                }
                else
                {
                    Keys(importer, RootEdges);
                    foreach (var field in RootEdges)
                    {
                        var declared = Strings(manifest, field);
                        var locked = Map(importer, field);
                        if (!declared.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(locked.Keys))
                            throw new InvalidDataException($"pnpm root declaration mismatch: {field}");
                        foreach (var (name, request) in declared)
                            Import(name, request, locked[name], snapshots);
                    }
                }
            }
            return new PnpmLock(packages, overrides);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new InvalidDataException($"pnpm lock {path}: {exception.Message}", exception);
        }
    }

    public static void ValidateArchiveMetadata(PnpmPackage package, JsonElement metadata)
    {
        if (Collectors.RequiredString(metadata, "name") != package.Name || Collectors.RequiredString(metadata, "version") != package.Version)
            throw new InvalidDataException("pnpm archive identity disagrees with lock.");
        var actualOptional = OptionalPeers(metadata, yaml: false);
        var declaredPeers = Strings(metadata, "peerDependencies");
        // pnpm materializes archive-declared optional peer metadata as "*" when
        // no range was supplied (for example debug's supports-color peer).
        foreach (var name in actualOptional) declaredPeers.TryAdd(name, "*");
        Same(declaredPeers, Strings(package.Metadata, "peerDependencies"), "peerDependencies");
        if (!actualOptional.SetEquals(OptionalPeers(package.Metadata, yaml: true)))
            throw new InvalidDataException("pnpm optional peer declarations disagree.");
        foreach (var field in PlatformFields)
            if (!Sequence(metadata, field).SequenceEqual(Sequence(package.Metadata, field)))
                throw new InvalidDataException($"pnpm platform declaration disagrees: {field}");
    }

    public void ValidateMetadata(PnpmPackage package, IReadOnlyDictionary<PnpmPackage, JsonElement> declarations)
    {
        var metadata = ArchiveMetadata(package, declarations);
        var actualOptional = OptionalPeers(metadata, yaml: false);
        var declaredPeers = Strings(package.Metadata, "peerDependencies");
        foreach (var (context, snapshot) in package.Snapshots)
        {
            var dependencies = Strings(metadata, "dependencies");
            var optional = Strings(metadata, "optionalDependencies");
            var peers = declaredPeers;
            var contextPeers = Identity(context).Peers.ToDictionary(peer => Identity(peer).Name, StringComparer.Ordinal);
            foreach (var name in optional.Keys) dependencies.Remove(name);
            var locked = Strings(snapshot, "dependencies");
            var lockedOptional = Strings(snapshot, "optionalDependencies");
            foreach (var name in lockedOptional.Keys)
                if (locked.ContainsKey(name)) throw new InvalidDataException($"Duplicate pnpm dependency edge: {context}/{name}");
            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, request) in dependencies.Concat(optional).Concat(peers))
            {
                var effective = Effective(package, name, request);
                if (effective == "-")
                {
                    if (locked.ContainsKey(name) || lockedOptional.ContainsKey(name) || contextPeers.ContainsKey(name))
                        throw new InvalidDataException($"Removed pnpm override remains in snapshot: {context}/{name}");
                    continue;
                }
                expected.Add(name);
                var optionalPeerOnly = actualOptional.Contains(name) && !dependencies.ContainsKey(name) && !optional.ContainsKey(name);
                var target = optional.ContainsKey(name) || optionalPeerOnly && lockedOptional.ContainsKey(name) ? lockedOptional : locked;
                if (!target.TryGetValue(name, out var reference))
                {
                    if (optionalPeerOnly && !contextPeers.ContainsKey(name)) continue;
                    throw new InvalidDataException($"pnpm archive declaration missing from snapshot: {context}/{name}");
                }
                var (_, resolved, _) = Identity(name + "@" + reference);
                if (peers.ContainsKey(name) && !dependencies.ContainsKey(name) && !optional.ContainsKey(name)
                    && (!contextPeers.TryGetValue(name, out var contextual) || contextual != name + "@" + reference))
                    throw new InvalidDataException($"pnpm peer context disagrees with resolved declaration: {context}/{name}");
                if (!NpmRange.Matches(resolved, effective))
                    throw new InvalidDataException($"pnpm resolved version violates declaration: {context}/{name}@{effective} -> {resolved}");
            }
            if (locked.Keys.Concat(lockedOptional.Keys).Any(name => !expected.Contains(name)))
                throw new InvalidDataException($"pnpm snapshot has undeclared dependency: {context}");
            var inherited = new HashSet<string>(StringComparer.Ordinal);
            var forwarded = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, reference) in locked.Concat(lockedOptional)
                .Where(pair => dependencies.ContainsKey(pair.Key) || optional.ContainsKey(pair.Key)))
            {
                var childKey = name + "@" + reference;
                var child = SnapshotPackage(package.Document, childKey);
                var childPeers = Identity(childKey).Peers.ToDictionary(peer => Identity(peer).Name, StringComparer.Ordinal);
                foreach (var peer in ExternalPeers(child, declarations)
                    .Concat(Sequence(child.Snapshots[childKey], "transitivePeerDependencies")).Distinct(StringComparer.Ordinal))
                {
                    if (peers.ContainsKey(peer) || dependencies.ContainsKey(peer) || optional.ContainsKey(peer) || peer == package.Name)
                    {
                        var supplied = peer == package.Name ? context
                            : locked.TryGetValue(peer, out var suppliedReference) ? peer + "@" + suppliedReference
                            : lockedOptional.TryGetValue(peer, out suppliedReference) ? peer + "@" + suppliedReference : null;
                        if (childPeers.TryGetValue(peer, out var childBinding) && childBinding != supplied)
                            throw new InvalidDataException($"pnpm child peer disagrees with local provider: {context}/{peer}");
                        continue;
                    }
                    if (!HasPeerSource(child, childKey, peer, declarations, new HashSet<string>(StringComparer.Ordinal)))
                        throw new InvalidDataException($"pnpm transitive peer lacks a declared source: {context}/{peer}");
                    inherited.Add(peer);
                    if (childPeers.TryGetValue(peer, out var binding))
                    {
                        if (forwarded.TryGetValue(peer, out var other) && other != binding)
                            throw new InvalidDataException($"Conflicting pnpm transitive peer bindings: {context}/{peer}");
                        forwarded[peer] = binding;
                    }
                }
            }
            var transitive = Sequence(snapshot, "transitivePeerDependencies").ToHashSet(StringComparer.Ordinal);
            if (!transitive.SetEquals(inherited))
                throw new InvalidDataException($"pnpm transitive peer declarations disagree with child requirements: {context}");
            foreach (var (name, binding) in forwarded)
                if (!contextPeers.TryGetValue(name, out var contextual) || contextual != binding)
                    throw new InvalidDataException($"pnpm forwarded peer context disagrees: {context}/{name}");
            foreach (var (name, contextual) in contextPeers)
            {
                var reference = locked.GetValueOrDefault(name) ?? lockedOptional.GetValueOrDefault(name);
                var binding = peers.ContainsKey(name) && reference is not null ? name + "@" + reference : forwarded.GetValueOrDefault(name);
                if (contextual != binding)
                    throw new InvalidDataException($"Unexplained pnpm peer context: {context}/{name}");
            }
        }
    }

    private PnpmPackage SnapshotPackage(int document, string key) =>
        Packages.Single(package => package.Document == document && package.Snapshots.ContainsKey(key));

    private static JsonElement ArchiveMetadata(PnpmPackage package, IReadOnlyDictionary<PnpmPackage, JsonElement> declarations) =>
        declarations.TryGetValue(package, out var metadata) ? metadata
            : throw new InvalidDataException($"Verified pnpm archive metadata unavailable: document={package.Document}:{package.Name}@{package.Version}");

    private HashSet<string> ExternalPeers(PnpmPackage package, IReadOnlyDictionary<PnpmPackage, JsonElement> declarations)
    {
        var metadata = ArchiveMetadata(package, declarations);
        var dependencies = Strings(metadata, "dependencies");
        var optional = Strings(metadata, "optionalDependencies");
        return Strings(package.Metadata, "peerDependencies")
            .Where(peer => Effective(package, peer.Key, peer.Value) != "-"
                && !dependencies.ContainsKey(peer.Key) && !optional.ContainsKey(peer.Key))
            .Select(peer => peer.Key).ToHashSet(StringComparer.Ordinal);
    }

    private bool HasPeerSource(PnpmPackage package, string key, string name,
        IReadOnlyDictionary<PnpmPackage, JsonElement> declarations, HashSet<string> visited)
    {
        if (ExternalPeers(package, declarations).Contains(name)) return true;
        if (!visited.Add(key) || !Sequence(package.Snapshots[key], "transitivePeerDependencies").Contains(name, StringComparer.Ordinal))
            return false;
        return Edges.SelectMany(field => Strings(package.Snapshots[key], field))
            .Any(edge =>
            {
                var childKey = edge.Key + "@" + edge.Value;
                return HasPeerSource(SnapshotPackage(package.Document, childKey), childKey, name, declarations, visited);
            });
    }

    internal (PnpmPackage Package, string Key) StoreContext(string folder)
    {
        var matches = Packages.SelectMany(package => package.Snapshots.Keys
            .Where(key => MatchesStoreFolder(key, folder)).Select(key => (Package: package, Key: key))).ToArray();
        if (matches.Length == 0)
            throw new InvalidDataException($"Unbound pnpm store context: {folder}");
        var first = matches[0];
        if (matches.Any(match => match.Key != first.Key
            || !JsonElement.DeepEquals(match.Package.Snapshots[match.Key], first.Package.Snapshots[first.Key])))
            throw new InvalidDataException($"Ambiguous pnpm store context: {folder}");
        return first;
    }

    private static bool MatchesStoreFolder(string key, string folder)
    {
        // pnpm 12 flattens nested peers before SHA256-shortening the store name.
        var filename = key.Replace('/', '+');
        if (filename.EndsWith(')')) filename = filename[..^1];
        filename = filename.Replace(")(", "_", StringComparison.Ordinal).Replace('(', '_').Replace(')', '_');
        var uppercase = filename.Any(char.IsAsciiLetterUpper);
        if (!uppercase && folder == filename) return true;
        var prefixLength = folder.Length - 33;
        if (prefixLength < 0 || prefixLength > filename.Length || !uppercase && filename.Length <= folder.Length
            || !folder.StartsWith(filename[..prefixLength] + "_", StringComparison.Ordinal))
            return false;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(filename)))[..32].ToLowerInvariant();
        return folder.EndsWith(digest, StringComparison.Ordinal);
    }

    private string Effective(PnpmPackage package, string name, string request)
    {
        if (package.Document == 0) return request;
        var matches = overrides.Where(pair =>
        {
            var (parent, child) = Selector(pair.Key);
            return child == name && (parent is null || parent == package.Name || parent == package.Name + "@" + package.Version);
        }).Select(pair => pair.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (matches.Length > 1) throw new InvalidDataException($"Ambiguous pnpm overrides: {package.Name}/{name}");
        return matches.SingleOrDefault() ?? request;
    }

    private static (string? Parent, string Child) Selector(string selector)
    {
        var parts = selector.Split('>');
        if (parts.Length > 2) throw new InvalidDataException($"Unsupported pnpm override: {selector}");
        Name(parts[^1]);
        if (parts.Length == 1) return (null, parts[0]);
        if (parts[0].LastIndexOf('@') > 0) Identity(parts[0]);
        else Name(parts[0]);
        return (parts[0], parts[1]);
    }

    private static void Import(string name, string request, JsonElement record, Dictionary<string, JsonElement> snapshots)
    {
        Name(name);
        Exact(request);
        Keys(record, "specifier", "version");
        var reference = Collectors.RequiredString(record, "version");
        if (Collectors.RequiredString(record, "specifier") != request || Identity(name + "@" + reference).Version != request
            || !snapshots.ContainsKey(name + "@" + reference))
            throw new InvalidDataException($"pnpm importer/manifest disagreement: {name}@{request}");
    }

    private static HashSet<string> OptionalPeers(JsonElement metadata, bool yaml)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, value) in Map(metadata, "peerDependenciesMeta"))
        {
            Name(name);
            Keys(value, "optional");
            if (value.TryGetProperty("optional", out var flag))
            {
                if (yaml && Text(flag) is not ("true" or "false")) throw new InvalidDataException($"Invalid pnpm peer flag: {name}");
                if (yaml ? Text(flag) == "true" : flag.GetBoolean()) result.Add(name);
            }
            if (!Strings(metadata, "peerDependencies").ContainsKey(name) && (yaml || !result.Contains(name)))
                throw new InvalidDataException($"pnpm optional peer lacks declaration: {name}");
        }
        return result;
    }

    private static string BaseIdentity(string context)
    {
        var (name, version, _) = Identity(context);
        return name + "@" + version;
    }

    private static (string Name, string Version, List<string> Peers) Identity(string value, int depth = 0)
    {
        if (depth > 32) throw new InvalidDataException("pnpm peer context depth limit exceeded.");
        var end = value.IndexOf('(');
        if (end < 0) end = value.Length;
        var plain = value[..end];
        var at = plain.LastIndexOf('@');
        if (at < 1) throw new InvalidDataException($"Invalid pnpm identity: {value}");
        var name = plain[..at];
        var version = plain[(at + 1)..];
        Name(name);
        Exact(version);
        var peers = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (end < value.Length)
        {
            if (value[end] != '(') throw new InvalidDataException($"Invalid pnpm peer context: {value}");
            var start = ++end;
            var level = 1;
            while (end < value.Length && level > 0)
            {
                if (value[end] == '(') level++;
                if (value[end] == ')') level--;
                end++;
            }
            if (level != 0) throw new InvalidDataException($"Unbalanced pnpm peer context: {value}");
            var peer = value[start..(end - 1)];
            var parsed = Identity(peer, depth + 1);
            if (!names.Add(parsed.Name)) throw new InvalidDataException($"Duplicate pnpm peer context: {value}");
            peers.Add(peer);
        }
        return (name, version, peers);
    }

    private static void Name(string name)
    {
        if (name.Length > 214 || !Regex.IsMatch(name, @"^(?:@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*$"))
            throw new InvalidDataException($"Unsafe/unsupported pnpm package name: {name}");
    }

    private static void Exact(string value)
    {
        if (!Regex.IsMatch(value, @"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$") || !NpmRange.Matches(value, value))
            throw new InvalidDataException($"Unsupported non-exact pnpm version: {value}");
    }

    internal static Dictionary<string, JsonElement> Map(JsonElement value, string field, bool required = false) =>
        value.TryGetProperty(field, out var map) ? Properties(map)
        : required ? throw new InvalidDataException($"Required pnpm mapping: {field}") : new(StringComparer.Ordinal);

    private static Dictionary<string, JsonElement> Properties(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected pnpm mapping.");
        return value.EnumerateObject().ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
    }

    internal static Dictionary<string, string> Strings(JsonElement value, string field) =>
        Map(value, field).ToDictionary(pair => pair.Key, pair => Text(pair.Value), StringComparer.Ordinal);

    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
        ? value.GetString()! : throw new InvalidDataException("Expected nonempty pnpm scalar.");

    private static JsonElement Required(JsonElement value, string field) => value.TryGetProperty(field, out var result)
        ? result : throw new InvalidDataException($"Required pnpm field: {field}");

    private static string[] Sequence(JsonElement value, string field) => value.TryGetProperty(field, out var array)
        ? array.EnumerateArray().Select(Text).ToArray() : [];

    private static void Keys(JsonElement value, params string[] supported)
    {
        foreach (var name in Properties(value).Keys)
            if (!supported.Contains(name, StringComparer.Ordinal)) throw new InvalidDataException($"Unsupported pnpm field: {name}");
    }

    private static void Same(Dictionary<string, string> left, Dictionary<string, string> right, string field)
    {
        if (!left.OrderBy(pair => pair.Key, StringComparer.Ordinal).SequenceEqual(right.OrderBy(pair => pair.Key, StringComparer.Ordinal)))
            throw new InvalidDataException($"pnpm declaration mismatch: {field}");
    }
}
