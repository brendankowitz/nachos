using System.Text;
using System.Text.Json;

namespace Nachos.DocsGen;

internal static class Generator
{
    internal static async Task GenerateAsync(Inputs inputs)
    {
        var api = inputs.Read(inputs.OpenApi);
        var manifest = inputs.Read(inputs.Manifest);
        var comparison = Compatibility.Compare(api, manifest);
        var options = OptionsReference.Render(inputs);
        _ = inputs.Read(inputs.Cli);
        _ = inputs.Read(Path.ChangeExtension(inputs.Cli, ".deps.json"));
        _ = inputs.Read(Path.ChangeExtension(inputs.Cli, ".runtimeconfig.json"));
        var help = await CliHelp.CaptureAsync(inputs.Cli, inputs.Root, TimeSpan.FromSeconds(10));
        var cli = new StringBuilder("---\ntitle: CLI reference\n---\n\n");
        cli.Append("Generated from real, help-only CLI invocations. No database or key operation was invoked.\n\n");
        foreach (var (command, text) in help)
        {
            cli.Append("## `nachos").Append(command.Length == 0 ? "" : " " + command).Append(" --help`\n\n");
            cli.Append("````text\n").Append(text).Append("````\n\n");
        }
        comparison["evidence"] = JsonSerializer.SerializeToNode(inputs.Evidence);
        comparison["cliHelpSha256"] = JsonSerializer.SerializeToNode(help.ToDictionary(
            entry => entry.Key.Length == 0 ? "--help" : entry.Key + " --help",
            entry => Inputs.Hash(Inputs.Utf8.GetBytes(entry.Value)), StringComparer.Ordinal));
        inputs.VerifyUnchanged();
        AtomicOutputs.Publish(inputs.Output, new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["rest-api.json"] = api,
            ["cli.md"] = Inputs.Utf8.GetBytes(cli.ToString()),
            ["options.md"] = Inputs.Utf8.GetBytes(options),
            ["compatibility.json"] = Inputs.Utf8.GetBytes(comparison.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true, NewLine = "\n" }) + "\n"),
        });
    }
}
