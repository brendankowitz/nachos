using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace Nachos.LicenseCheck;

internal static class Artifacts
{
    internal sealed record Inventory(Dictionary<string, HashSet<string>> Scopes, bool Complete, DocsProvenanceReport? Docs);

    public static Inventory Collect(AuditInputs inputs, List<PackageEvidence> packages, List<string> errors)
    {
        var initialErrors = errors.Count;
        var shipped = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        DocsProvenanceReport? docs = null;
        Collectors.Capture(() => Publish(inputs, inputs.ApiPublish, "Nachos.Api", "api", packages, shipped), inputs.ApiPublish, errors);
        Collectors.Capture(() => Publish(inputs, inputs.CliPublish, "Nachos.Cli", "cli", packages, shipped), inputs.CliPublish, errors);
        var site = Path.Combine(inputs.Root, "docs", "site");
        if (Directory.Exists(site) || inputs.DocsOutputRoot is not null)
        {
            Collectors.Capture(() =>
            {
                var verified = DocsProvenance.Verify(new DocsProvenanceInputs(site, Path.Combine(inputs.Root, "docs", "assets"),
                    site, inputs.DocsOutputRoot ?? Path.Combine(site, "dist"), inputs.DocsSite, inputs.DocsBase));
                foreach (var entry in verified.Packages)
                    Add("npm", entry.Package, entry.Version, "docs", packages, shipped);
                docs = verified;
            }, "docs output provenance (including .nachos/bundle-modules.json)", errors);
        }
        return new Inventory(shipped, errors.Count == initialErrors, docs);
    }

    private static void Publish(AuditInputs inputs, string root, string application, string scope,
        List<PackageEvidence> packages, Dictionary<string, HashSet<string>> shipped)
    {
        root = Path.GetFullPath(root);
        var depsPath = Path.Combine(root, application + ".deps.json");
        using var deps = JsonDocument.Parse(File.ReadAllText(depsPath));
        var firstParty = new HashSet<string>(StringComparer.Ordinal);
        foreach (var library in deps.RootElement.GetProperty("libraries").EnumerateObject())
        {
            var slash = library.Name.LastIndexOf('/');
            if (slash <= 0)
            {
                throw new InvalidDataException($"Malformed deps.json library: {library.Name}");
            }
            var name = library.Name[..slash];
            var version = library.Name[(slash + 1)..];
            var type = Collectors.RequiredString(library.Value, "type");
            if (type == "package")
            {
                Add("nuget", name, version, scope, packages, shipped);
            }
            else if (type == "project")
            {
                var sources = Directory.EnumerateFiles(Path.Combine(inputs.Root, "src"), name + ".csproj", SearchOption.AllDirectories);
                if (!sources.Any())
                {
                    throw new InvalidDataException($"Unknown first-party project provenance in deps.json: {name}");
                }
                firstParty.Add(name + ".dll");
                firstParty.Add(name + ".pdb");
            }
            else
            {
                throw new InvalidDataException($"Unproven deps.json library type: {type}");
            }
        }
        if (!firstParty.Contains(application + ".dll") || !File.Exists(Path.Combine(root, application + ".dll")))
        {
            throw new InvalidDataException($"Missing published application evidence: {application} project library and assembly are required.");
        }
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray();
        var fingerprints = files.ToDictionary(file => Collectors.Under(root, Collectors.Relative(root, file)), HashFile, Collectors.PathComparer);
        var unknown = new Dictionary<string, string>(Collectors.PathComparer);
        var sourceNotices = new HashSet<string>(Collectors.PathComparer);
        foreach (var file in files)
        {
            var relative = Collectors.Relative(root, file);
            Collectors.Under(root, relative);
            if (DacFxApproval.IsSourceNotice(inputs.Root, relative, fingerprints[file])
                || MicrosoftPrimaryApproval.IsSourceNotice(inputs.Root, relative, fingerprints[file]))
            {
                sourceNotices.Add(file);
                continue;
            }
            if (relative == application + ".deps.json" || relative == application + ".runtimeconfig.json"
                || relative == application || relative == application + ".exe" || firstParty.Contains(relative))
            {
                continue;
            }
            if (relative == application + ".staticwebassets.endpoints.json")
            {
                using var endpoints = JsonDocument.Parse(File.ReadAllText(file));
                var manifest = endpoints.RootElement;
                if (manifest.EnumerateObject().Count() == 3 && manifest.GetProperty("Version").GetInt32() == 1
                    && manifest.GetProperty("ManifestType").GetString() == "Publish" && manifest.GetProperty("Endpoints").GetArrayLength() == 0)
                {
                    continue;
                }
            }
            if (relative == "web.config" && XNode.DeepEquals(XDocument.Parse(File.ReadAllText(file)).Root, XDocument.Parse($"""
                <configuration><location path="." inheritInChildApplications="false"><system.webServer>
                <handlers><add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" /></handlers>
                <aspNetCore processPath="dotnet" arguments=".\{application}.dll" stdoutLogEnabled="false" stdoutLogFile=".\logs\stdout" hostingModel="inprocess" />
                </system.webServer></location></configuration>
                """).Root))
            {
                continue;
            }
            var source = Path.Combine(inputs.Root, "src", application, relative);
            if (relative.StartsWith("appsettings", StringComparison.Ordinal) && relative.EndsWith(".json", StringComparison.Ordinal)
                && File.Exists(source) && HashFile(source) == fingerprints[file])
            {
                continue;
            }
            unknown.Add(file, fingerprints[file]);
        }
        // Content copied by build targets may not be listed in deps.json. Match its bytes
        // against *all* resolved nupkgs, not only the runtime dependency graph.
        var attributed = new HashSet<string>(Collectors.PathComparer);
        var lengths = files.Select(file => new FileInfo(file).Length).ToHashSet();
        foreach (var package in packages.Where(package => package.Ecosystem == "nuget" && package.Archive is not null))
        {
            using var zip = ZipFile.OpenRead(package.Archive!);
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries.Where(entry => lengths.Contains(entry.Length)))
            {
                using var stream = entry.Open();
                hashes.Add(Convert.ToHexString(SHA256.HashData(stream)));
            }
            var matches = fingerprints.Where(file => !sourceNotices.Contains(file.Key) && hashes.Contains(file.Value)).Select(file => file.Key).ToArray();
            if (matches.Length > 0)
            {
                Mark(package.Key, scope, shipped);
                foreach (var match in matches)
                {
                    attributed.Add(match);
                }
            }
        }
        var unattributed = unknown.Keys.Except(attributed, Collectors.PathComparer).ToArray();
        if (unattributed.Length > 0)
        {
            throw new InvalidDataException("Unproven copied artifact provenance: " + string.Join(", ", unattributed.Select(file => Collectors.Relative(root, file))));
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void Add(string ecosystem, string name, string version, string scope,
        List<PackageEvidence> packages, Dictionary<string, HashSet<string>> shipped)
    {
        var key = PackageEvidence.Identity(ecosystem, name, version);
        if (!packages.Any(package => package.Key == key))
        {
            throw new InvalidDataException($"Artifact package has no locked license inventory: {key}");
        }
        Mark(key, scope, shipped);
    }

    private static void Mark(string key, string scope, Dictionary<string, HashSet<string>> shipped)
    {
        if (!shipped.TryGetValue(key, out var scopes))
        {
            scopes = new HashSet<string>(StringComparer.Ordinal);
            shipped.Add(key, scopes);
        }
        scopes.Add(scope);
    }
}
