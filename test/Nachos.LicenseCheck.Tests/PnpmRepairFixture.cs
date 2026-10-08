using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace Nachos.LicenseCheck.Tests;

internal sealed class PnpmRepairFixture : IDisposable
{
    public PnpmFixture Base { get; } = new();
    public AuditFixture Audit => Base.Audit;
    public JsonObject[] Documents { get; }
    public JsonObject Packages => Documents[1]["packages"]!.AsObject();
    public JsonObject Snapshots => Documents[1]["snapshots"]!.AsObject();
    public const string Consumer = "consumer@1.0.0(peer@2.0.0)";

    public PnpmRepairFixture()
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Base.Lock)));
        Documents = yaml.Documents.Select(document => Convert(document.RootNode).AsObject()).ToArray();
    }

    public void Save() => Audit.WriteText("web/pnpm-lock.yaml",
        string.Join("\n---\n", Documents.Select(document => document.ToJsonString())));

    public JsonObject Metadata(string name)
    {
        using var file = File.OpenRead(Base.Archives[name]);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
            if (entry.Name == "package/package.json")
            {
                using var reader = new StreamReader(entry.DataStream!);
                return JsonNode.Parse(reader.ReadToEnd())!.AsObject();
            }
        throw new InvalidDataException("Fixture archive metadata missing.");
    }

    public void Rewrite(string name, Action<JsonObject> mutate)
    {
        var metadata = Metadata(name);
        var oldKey = metadata["name"]!.GetValue<string>() + "@" + metadata["version"]!.GetValue<string>();
        mutate(metadata);
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionMode.Compress, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            Add("package.json", metadata.ToJsonString());
            Add("LICENSE", AuditFixture.Mit);
            void Add(string path, string content)
            {
                using var data = new MemoryStream(Encoding.UTF8.GetBytes(content));
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "package/" + path) { DataStream = data });
            }
        }
        var digest = SHA512.HashData(bytes.ToArray());
        var path = Audit.Full("npm-archives/" + System.Convert.ToHexString(SHA256.HashData(digest)).ToLowerInvariant() + ".tgz");
        File.WriteAllBytes(path, bytes.ToArray());
        Base.Archives[name] = path;
        foreach (var document in Documents)
            if (document["packages"]![oldKey] is { } record)
                record["resolution"]!["integrity"] = "sha512-" + System.Convert.ToBase64String(digest);
    }

    public string Install(string context, string slot, string archiveName)
    {
        var path = "web/node_modules/.pnpm/" + context + "/node_modules/" + slot;
        Audit.Write(path + "/package.json", Metadata(archiveName));
        Audit.WriteText(path + "/LICENSE", AuditFixture.Mit);
        return Audit.Full(path);
    }

    public void Link(string context, string slot, string target)
    {
        var path = Audit.Full("web/node_modules/.pnpm/" + (context.Length == 0 ? "" : context + "/") + "node_modules/" + slot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Directory.CreateSymbolicLink(path, target);
    }

    public async Task<(int Exit, string Output)> Run()
    {
        Save();
        return await PnpmTests.Command(Base, "--repo", Audit.Root, "--nuget-inventory", Audit.Full("nuget.json"),
            "--nuget-cache", Audit.Full("cache"), "--api-publish", Audit.Full("artifacts/api"),
            "--cli-publish", Audit.Full("artifacts/cli"), "--npm-archives", Audit.Full("npm-archives"),
            "--report", Audit.Full("repair-report.json"));
    }

    public static void Rename(JsonObject map, string oldKey, string newKey)
    {
        var value = map[oldKey];
        map.Remove(oldKey);
        map.Add(newKey, value);
    }

    private static JsonNode Convert(YamlNode node) => node switch
    {
        YamlScalarNode scalar => JsonValue.Create(scalar.Value)!,
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(Convert).ToArray()),
        YamlMappingNode mapping => new JsonObject(mapping.Children.Select(pair =>
            new KeyValuePair<string, JsonNode?>(((YamlScalarNode)pair.Key).Value!, Convert(pair.Value)))),
        _ => throw new InvalidDataException("Unexpected fixture YAML node.")
    };

    public void Dispose() => Base.Dispose();
}
