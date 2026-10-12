using System.Globalization;
using System.Reflection;
using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Nachos.Cli;
using Nachos.Core.Configuration;
using Nachos.DataLayer.SqlServer.Schema;
using Shouldly;

namespace Nachos.DocsGen.Tests;

public sealed class RealProducerTests
{
    internal static string Root => Environment.GetEnvironmentVariable("NACHOS_DOCSGEN_TEST_ROOT")
        ?? throw new InvalidOperationException("Set NACHOS_DOCSGEN_TEST_ROOT to the prepared repository.");

    internal static string Configuration => typeof(RealProducerTests).Assembly
        .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
        ?? throw new InvalidOperationException("The test assembly must declare its build configuration.");

    [Fact]
    public void DefaultInputs_UseTheRunningProducerConfiguration()
    {
        Inputs.Parse(["--root", Root]).Configuration.ShouldBe(Configuration);
    }

    [Fact]
    public void BuiltProducer_KeepsGeneratedProductsOutsideAutomationSources()
    {
        var artifacts = Path.Combine(Root, "artifacts", "docsgen");
        File.Exists(Path.Combine(artifacts, "bin", Configuration, "net10.0", "DocsGen.dll")).ShouldBeTrue();
        var restoreMetadata = Path.Combine(Root, "eng", "DocsGen", "obj");
        File.Exists(Path.Combine(restoreMetadata, "project.assets.json")).ShouldBeTrue();
        foreach (var name in new[] { "DocsGen.csproj.nuget.g.props", "DocsGen.csproj.nuget.g.targets", "project.nuget.cache", "DocsGen.csproj.nuget.dgspec.json" })
            File.Exists(Path.Combine(restoreMetadata, name)).ShouldBeTrue();
        foreach (var name in new[] { "Nachos.Core", "Nachos.Abstractions", "Nachos.DataLayer.SqlServer" })
            File.Exists(Path.Combine(artifacts, "obj", "options-xml", Configuration, name + ".xml")).ShouldBeTrue();
        Directory.Exists(Path.Combine(Root, "eng", "DocsGen", "bin")).ShouldBeFalse();
        Directory.EnumerateFiles(Path.Combine(Root, "eng", "DocsGen"), "*", SearchOption.AllDirectories)
            .Where(file => new[] { ".dll", ".exe", ".so", ".dylib", ".node", ".pdb" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .Select(file => Path.GetRelativePath(Root, file))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task GeneratesAllReferenceFiles_FromRealBuiltProducers()
    {
        var output = Path.Combine(Root, "test", "Nachos.DocsGen.Tests", "obj", "real-output");
        await Generator.GenerateAsync(Inputs.Parse(["--configuration", Configuration, "--root", Root, "--output", output]));
        Directory.GetFiles(output).Select(Path.GetFileName).Order(StringComparer.Ordinal).ShouldBe(
            ["cli.md", "compatibility.json", "options.md", "rest-api.json"]);

        File.ReadAllBytes(Path.Combine(output, "rest-api.json")).ShouldBe(
            File.ReadAllBytes(Path.Combine(Root, "src", "Nachos.Api", "obj", "Nachos.Api.json")));
        var cli = File.ReadAllText(Path.Combine(output, "cli.md"));
        // Independent in-process invocation of the real CLI, not the generator's parser.
        var commands = Commands().ToArray();
        cli.Split("## `nachos", StringSplitOptions.None).Length.ShouldBe(commands.Length + 1);
        foreach (var tokens in commands)
        {
            using var help = new StringWriter(CultureInfo.InvariantCulture);
            using var helpError = new StringWriter(CultureInfo.InvariantCulture);
            (await CliApp.RunAsync([.. tokens, "--help"], help, helpError, CancellationToken.None)).ShouldBe(0);
            // RootCommand uses the entry assembly name: only this usage label differs in testhost.
            var expected = help.ToString().ReplaceLineEndings("\n")
                .Replace("\n  " + new RootCommand().Name, "\n  Nachos.Cli", StringComparison.Ordinal).TrimEnd();
            cli.Contains(expected, StringComparison.Ordinal).ShouldBeTrue();
            helpError.ToString().ShouldBeEmpty();
        }

        var options = File.ReadAllText(Path.Combine(output, "options.md"));
        options.ShouldContain($"| `Nachos:Summary:MessagesPerShort` | `int` | `{new NachosOptions().Summary.MessagesPerShort}` |");
        options.ShouldContain($"| `{SqlServerOptions.SectionName}:AutomaticSchemaDeploymentEnabled` | `bool` | `false` |");
        options.ShouldContain("Maximum o200k_base tokens");
        options.ShouldContain("No member XML summary");
        options.ShouldContain("ConnectionStrings:nachos");
        options.ShouldContain("canonical key wins");
        options.ShouldNotContain("does not bind or enable SQL");
        options.ShouldContain("NachosKey and Microsoft Entra");
        options.ShouldNotContain("inspected baseline");
        options.ShouldNotContain("direct configuration read");
        var rows = options.Split('\n').Where(line => line.StartsWith("| `Nachos:", StringComparison.Ordinal))
            .Select(line => line.Split('|').Select(cell => cell.Trim().Trim('`')).ToArray())
            .ToDictionary(cells => cells[1], cells => cells[3], StringComparer.Ordinal);
        var defaults = Defaults(new NachosOptions(), "Nachos")
            .Concat(Defaults(new SqlServerOptions(), SqlServerOptions.SectionName)).ToArray();
        rows.Count.ShouldBe(defaults.Length);
        foreach (var (path, value) in defaults) rows[path].ShouldBe(JsonSerializer.Serialize(value));

        var compatibility = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "compatibility.json")))!;
        compatibility["sdkConformance"]!.GetValue<string>().ShouldBe("not-run");
        compatibility["authentication"]!.GetValue<string>().ShouldBe("not-assessed");
        var evidence = compatibility["evidence"]!.AsObject();
        var cliPath = $"src/Nachos.Cli/bin/{Configuration}/net10.0/Nachos.Cli.dll";
        evidence[cliPath]!.GetValue<string>().ShouldBe(Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(Path.Combine(Root, cliPath)))));
        foreach (var name in new[] { "Nachos.Core", "Nachos.Abstractions", "Nachos.DataLayer.SqlServer" })
        {
            var xmlPath = $"artifacts/docsgen/obj/options-xml/{Configuration}/{name}.xml";
            evidence[xmlPath]!.GetValue<string>().ShouldBe(Convert.ToHexStringLower(
                SHA256.HashData(File.ReadAllBytes(Path.Combine(Root, xmlPath)))));
        }
        var otherConfiguration = Configuration == "Release" ? "Debug" : "Release";
        evidence.Select(entry => entry.Key).ShouldNotContain(path => path.Contains(
            "/" + otherConfiguration + "/", StringComparison.Ordinal));
        var counts = compatibility["counts"]!;
        counts["actualOperations"]!.GetValue<int>().ShouldBe(56);
        counts["declaredImplemented"]!.GetValue<int>().ShouldBe(21);
        counts["declaredPartial"]!.GetValue<int>().ShouldBe(1);
        counts["staged501"]!.GetValue<int>().ShouldBe(34);
        counts["missing"]!.GetValue<int>().ShouldBe(1);
        counts["extensions"]!.GetValue<int>().ShouldBe(2);
        foreach (var path in new[] { "/v3/keys", "/v3/admin/grants" })
            compatibility["routes"]!.AsArray().Single(row => row!["path"]!.GetValue<string>() == path)!
                ["status"]!.GetValue<string>().ShouldBe("declared-implemented");
        var missing = compatibility["routes"]!.AsArray().Single(r => r!["path"]!.GetValue<string>() == "/health")!;
        missing["status"]!.GetValue<string>().ShouldBe("missing");
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "rest-api.json")));
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        foreach (var operation in path.Value.EnumerateObject().Where(item =>
                     item.Name is "get" or "post" or "put" or "delete" or "patch" or "options" or "head" or "trace"))
        {
            var method = operation.Name.ToUpperInvariant();
            var row = compatibility["routes"]!.AsArray().Single(row =>
                row!["path"]!.GetValue<string>() == path.Name && row["method"]!.GetValue<string>() == method)!;
            row["responses"]!.AsArray().Select(code => code!.GetValue<string>()).ShouldBe(
                operation.Value.GetProperty("responses").EnumerateObject().Select(response => response.Name).Order(StringComparer.Ordinal));
        }
        foreach (var name in new[] { "cli.md", "options.md", "compatibility.json" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(output, name));
            bytes.Take(3).SequenceEqual(new byte[] { 239, 187, 191 }).ShouldBeFalse();
            var text = Inputs.Utf8.GetString(bytes);
            text.ShouldNotContain("\r");
            text.ShouldNotContain(Root);
        }
    }

    [Fact]
    public async Task RealInputs_ProduceIdenticalBytesOnRepeat()
    {
        var output = Path.Combine(Root, "test", "Nachos.DocsGen.Tests", "obj", "deterministic-output");
        (await Program.RunAsync(["--configuration", Configuration, "--root", Root, "--output", output], TextWriter.Null, TextWriter.Null)).ShouldBe(0);
        var first = Directory.GetFiles(output).ToDictionary(file => Path.GetFileName(file), File.ReadAllBytes);
        (await Program.RunAsync(["--configuration", Configuration, "--root", Root, "--output", output], TextWriter.Null, TextWriter.Null)).ShouldBe(0);
        foreach (var file in Directory.GetFiles(output))
            File.ReadAllBytes(file).ShouldBe(first[Path.GetFileName(file)]);
    }

    private static IEnumerable<string[]> Commands()
    {
        yield return [];
        // These are the actual first-party, pure command factories. No action is invoked.
        var factories = typeof(CliApp).Assembly.GetTypes()
            .Where(type => type.Namespace == "Nachos.Cli" && type.Name.EndsWith("Commands", StringComparison.Ordinal))
            .Select(type => type.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes))
            .Where(method => method?.ReturnType == typeof(Command));
        foreach (var factory in factories)
            foreach (var path in Walk((Command)factory!.Invoke(null, null)!, [])) yield return path;
    }

    private static IEnumerable<string[]> Walk(Command command, string[] parent)
    {
        var path = parent.Append(command.Name).ToArray();
        yield return path;
        foreach (var child in command.Subcommands)
            foreach (var descendant in Walk(child, path)) yield return descendant;
    }

    private static IEnumerable<(string Path, object? Value)> Defaults(object options, string prefix)
    {
        foreach (var property in options.GetType().GetProperties())
        {
            var value = property.GetValue(options);
            var path = prefix + ":" + property.Name;
            if (value is null or string or bool or int) yield return (path, value);
            else foreach (var child in Defaults(value, path)) yield return child;
        }
    }
}
