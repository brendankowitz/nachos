using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class DocsProvenanceTests
{
    [Theory]
    [InlineData("original")]
    [InlineData("stylesheet")]
    [InlineData("inline")]
    public void ReviewedPortablePositive_ValidatesWithoutNode(string variant)
    {
        using var fixture = new ProtocolFixture(variant);
        var result = fixture.Verify();
        result.OutputCount.ShouldBe(6);
        result.InputCount.ShouldBe(10);
        result.SourceCount.ShouldBe(6);
        result.Packages.ShouldBe(new[] { new DocsPackage("fixture-content", "1.2.3") });
    }

    public static IEnumerable<object[]> NegativeNames() => ProtocolFixture.Negatives()
        .Select(item => new object[] { item!["name"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(NegativeNames))]
    public void ReviewedPortableNegative_FailsProvenanceSpecifically(string name)
    {
        var mutation = ProtocolFixture.Negatives().Single(item => item!["name"]!.GetValue<string>() == name)!;
        using var fixture = new ProtocolFixture(mutation["base"]?.GetValue<string>() ?? "original");
        foreach (var change in mutation["mutations"]!.AsArray())
        {
            var path = fixture.Full(change!["path"]!.GetValue<string>());
            if (change["action"]!.GetValue<string>() == "remove") { File.Delete(path); }
            else { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, change["text"]!.GetValue<string>()); }
        }
        // Each record names the diagnostic of its own check; the shared prefix alone also matches a broken reader.
        Should.Throw<InvalidDataException>(() => fixture.Verify()).Message
            .ShouldContain("Docs provenance: " + mutation["expect"]!.GetValue<string>(), Case.Sensitive);
    }

    [Theory]
    [InlineData("site", "https://example.test")]
    [InlineData("base", "/another")]
    public void SettingsComeFromCallerNotTheDocument(string field, string value)
    {
        using var fixture = new ProtocolFixture();
        var inputs = fixture.Inputs;
        inputs = field == "site" ? inputs with { Site = value } : inputs with { Base = value };
        Should.Throw<InvalidDataException>(() => DocsProvenance.Verify(inputs)).Message.ShouldContain("settings");
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("C:/drive")]
    [InlineData("bad\\path")]
    [InlineData("bad//path")]
    [InlineData("dir/CON.txt")]
    [InlineData("dir/lpt0.txt")]
    [InlineData("dir/end.")]
    [InlineData("dir/end ")]
    [InlineData("dir/colon:name")]
    [InlineData("caf\u00e9.txt")]
    [InlineData("cafe\u0301.txt")]
    public void UnsafeOrUnprovenUnicodePathsFailClosed(string path)
    {
        using var fixture = new ProtocolFixture();
        fixture.Edit(document => document["outputs"]![0]!["path"] = path);
        Should.Throw<InvalidDataException>(() => fixture.Verify()).Message.ShouldContain("path");
    }

    [Theory]
    [InlineData("top")]
    [InlineData("build")]
    [InlineData("input")]
    [InlineData("source")]
    [InlineData("output")]
    [InlineData("evidence")]
    [InlineData("package")]
    public void UnknownScopeOrCompletenessFieldsAreRejected(string location)
    {
        using var fixture = new ProtocolFixture();
        fixture.Edit(document =>
        {
            var node = location switch
            {
                "top" => document, "build" => document["build"]!, "input" => document["inputs"]![0]!,
                "source" => document["sources"]![0]!, "output" => document["outputs"]![0]!,
                "evidence" => document["outputs"]![0]!["evidence"]![0]!,
                _ => document["outputs"]![0]!["packages"]![0]!
            };
            node["complete"] = true;
        });
        Should.Throw<InvalidDataException>(() => fixture.Verify()).Message.ShouldContain("fields");
    }

    [Fact]
    public void DuplicateJsonFieldsAreNotLastValueWins()
    {
        using var fixture = new ProtocolFixture();
        var path = fixture.Full(ProtocolFixture.Manifest);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1", StringComparison.Ordinal));
        Should.Throw<InvalidDataException>(() => fixture.Verify()).Message.ShouldContain("duplicate");
    }

    [Fact]
    public void SemanticEvidenceDuplicatesIgnoreJsonPropertyOrder()
    {
        using var fixture = new ProtocolFixture();
        fixture.Edit(document =>
        {
            var records = document["outputs"]![0]!["evidence"]!.AsArray();
            var original = records[0]!;
            records.Add(new JsonObject
            {
                ["sha256"] = original["sha256"]!.DeepClone(),
                ["sources"] = original["sources"]!.DeepClone(),
                ["producer"] = original["producer"]!.DeepClone()
            });
        });
        Should.Throw<InvalidDataException>(() => fixture.Verify()).Message.ShouldContain("duplicate evidence");
    }

    [Fact]
    public void EverySourceMustBeReachableFromOutputEvidence()
    {
        using var fixture = new ProtocolFixture();
        fixture.Edit(document =>
        {
            var sources = document["sources"]!.AsArray();
            sources.Add(new JsonObject
            {
                ["path"] = "assets/logo.svg", ["sha256"] = document["inputs"]![0]!["sha256"]!.DeepClone(), ["kind"] = "first-party"
            });
            document["sources"] = new JsonArray(sources.OrderBy(item => item!["path"]!.GetValue<string>(), StringComparer.Ordinal)
                .Select(item => item!.DeepClone()).ToArray());
        });
        Should.Throw<InvalidDataException>(() => fixture.Verify()).Message.ShouldContain("unreferenced");
    }

    [Fact]
    public void CSharpNeverSearchesAncestorNodeModules()
    {
        using var fixture = new ProtocolFixture();
        Directory.Move(fixture.Full("site/node_modules"), fixture.Full("node_modules"));
        Should.Throw<InvalidDataException>(() => fixture.Verify()).Message
            .ShouldContain("Docs provenance: Missing or case-aliased evidence path: node_modules/fixture-content/client.js", Case.Sensitive);
    }

    [Fact]
    public void NestedPackageManifestCannotBorrowParentOwnership()
    {
        using var fixture = new ProtocolFixture();
        fixture.Write("site/node_modules/fixture-content/nested/package.json", """{"name":"another","version":"1.2.3"}""");
        File.Move(fixture.Full("site/node_modules/fixture-content/client.js"), fixture.Full("site/node_modules/fixture-content/nested/client.js"));
        fixture.Edit(document =>
        {
            document["sources"]![0]!["path"] = "npm/node_modules/fixture-content/nested/client.js";
            document["sources"] = new JsonArray(document["sources"]!.AsArray().OrderBy(item => item!["path"]!.GetValue<string>(), StringComparer.Ordinal)
                .Select(item => item!.DeepClone()).ToArray());
            document["outputs"]![0]!["evidence"]![0]!["sources"]![0] = "npm/node_modules/fixture-content/nested/client.js";
        });
        Should.Throw<InvalidDataException>(() => fixture.Verify()).Message.ShouldContain("owner");
    }

    [Theory]
    [InlineData("site/src/new.md")]
    [InlineData("assets/new.svg")]
    [InlineData("site/dist/.hidden/new.bin")]
    [InlineData("site/dist/.nachos/unlisted.json")]
    public void NewFilesCannotDisappearFromFixedClosures(string path)
    {
        using var fixture = new ProtocolFixture();
        fixture.Write(path, "added");
        Should.Throw<InvalidDataException>(() => fixture.Verify()).Message.ShouldContain("closure");
    }

    [Fact]
    public void PhysicalCaseAliasesAreRejectedEvenIfTheFileSystemWouldResolveThem()
    {
        using var fixture = new ProtocolFixture();
        fixture.Edit(document => document["sources"]![0]!["path"] = "npm/node_modules/fixture-content/CLIENT.js");
        Should.Throw<InvalidDataException>(() => fixture.Verify()).Message
            .ShouldContain("Docs provenance: Missing or case-aliased evidence path: node_modules/fixture-content/CLIENT.js", Case.Sensitive);
    }

    [Fact]
    public void LinkedRootIsNotTrustedMerelyBecauseCallerSelectedIt()
    {
        using var fixture = new ProtocolFixture();
        var link = fixture.Full("linked-site");
        Directory.CreateSymbolicLink(link, fixture.Full("site"));
        try { Should.Throw<InvalidDataException>(() => DocsProvenance.Verify(fixture.Inputs with { SiteRoot = link })).Message.ShouldContain("link"); }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData("package-union", "Output package union differs from referenced sources: client.js")]
    [InlineData("lock-name", Identity)]
    [InlineData("lock-version", Identity)]
    [InlineData("manifest-name", Identity)]
    [InlineData("manifest-hash", Identity)]
    [InlineData("source-hash", "Source hash differs: npm/node_modules/fixture-content/client.js")]
    public void OwnershipAndDerivedUnionsCannotBeReplacedByClaims(string change, string expected)
    {
        using var fixture = new ProtocolFixture();
        if (change is "lock-name" or "lock-version")
        {
            var path = fixture.Full("site/package-lock.json");
            var value = JsonNode.Parse(File.ReadAllText(path))!;
            value["packages"]!["node_modules/fixture-content"]![change == "lock-name" ? "name" : "version"] = "different";
            File.WriteAllText(path, value.ToJsonString());
            fixture.Edit(document => document["inputs"]!.AsArray().Single(item => item!["path"]!.GetValue<string>() == "site/package-lock.json")!["sha256"] = Hash(path));
        }
        else if (change == "manifest-name")
        {
            var path = fixture.Full("site/node_modules/fixture-content/package.json");
            var value = JsonNode.Parse(File.ReadAllText(path))!;
            value["name"] = "different";
            File.WriteAllText(path, value.ToJsonString());
            fixture.Edit(document =>
            {
                foreach (var source in document["sources"]!.AsArray().Where(item => item!["kind"]!.GetValue<string>() == "package"))
                    source!["packageJsonSha256"] = Hash(path);
            });
        }
        else fixture.Edit(document =>
        {
            if (change == "package-union") document["outputs"]![0]!["packages"] = new JsonArray();
            else document["sources"]![0]![change == "source-hash" ? "sha256" : "packageJsonSha256"] = new string('0', 64);
        });
        Should.Throw<InvalidDataException>(() => fixture.Verify()).Message.ShouldContain("Docs provenance: " + expected, Case.Sensitive);
    }

    [Theory]
    [InlineData("site/src/index.html")]
    [InlineData("site/dist/index.html")]
    [InlineData("site/node_modules/fixture-content/client.js")]
    [InlineData("site/dist/.nachos/output-provenance.v1.json")]
    public void LinkedEvidenceFilesIncludingMetadataAreRejected(string relative)
    {
        using var fixture = new ProtocolFixture();
        var target = fixture.Full("saved-file");
        File.Move(fixture.Full(relative), target);
        File.CreateSymbolicLink(fixture.Full(relative), target);
        try { Should.Throw<InvalidDataException>(() => fixture.Verify()).Message.ShouldContain("link"); }
        finally { File.Delete(fixture.Full(relative)); }
    }

    [Theory]
    [InlineData("site/src")]
    [InlineData("site/public")]
    [InlineData("site/integrations")]
    [InlineData("site/scripts")]
    [InlineData("assets")]
    public void FixedInputTreesCannotBeMissingOrEmpty(string relative)
    {
        using var fixture = new ProtocolFixture();
        Directory.Delete(fixture.Full(relative), true);
        Directory.CreateDirectory(fixture.Full(relative));
        Should.Throw<InvalidDataException>(() => fixture.Verify()).Message.ShouldContain("empty");
    }

    private const string Identity = "Package source/nearest manifest/lock identity differs: npm/node_modules/fixture-content/client.js";

    private static string Hash(string path) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}

internal sealed class ProtocolFixture : IDisposable
{
    public const string Manifest = "site/dist/.nachos/output-provenance.v1.json";
    public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "fixtures-work", Guid.NewGuid().ToString("N"));
    public DocsProvenanceInputs Inputs => new(Full("site"), Full("assets"), Full("site"), Full("site/dist"));
    public ProtocolFixture(string variant = "original")
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "docs-provenance-positive.json")));
        foreach (var entry in document.RootElement.GetProperty(variant).EnumerateObject())
        {
            var path = Full(entry.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Convert.FromBase64String(entry.Value.GetString()!));
        }
    }
    public static JsonArray Negatives() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "docs-provenance-negative.json")))!.AsArray();
    public DocsProvenanceReport Verify() => DocsProvenance.Verify(Inputs);
    public string Full(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    public void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(Full(path))!); File.WriteAllText(Full(path), text); }
    public void Edit(Action<JsonNode> action)
    {
        var document = JsonNode.Parse(File.ReadAllText(Full(Manifest)))!;
        action(document);
        File.WriteAllText(Full(Manifest), document.ToJsonString());
    }
    public void Dispose() => Directory.Delete(Root, true);
}
