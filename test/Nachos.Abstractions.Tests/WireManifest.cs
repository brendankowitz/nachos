using System.Text.Json;

namespace Nachos.Abstractions.Tests;

/// <summary>Loads <c>test/contracts/honcho-v3-wire.json</c> by walking up from the test assembly.</summary>
internal static class WireManifest
{
    private static readonly Lazy<JsonDocument> Document = new(Load);

    public static IReadOnlySet<string> Properties(string schema) =>
        Schema(schema).GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> Required(string schema) =>
        Schema(schema).GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);

    private static JsonElement Schema(string name) =>
        Document.Value.RootElement.GetProperty("schemas").GetProperty(name);

    private static JsonDocument Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "test", "contracts", "honcho-v3-wire.json");
            if (File.Exists(candidate))
            {
                return JsonDocument.Parse(File.ReadAllBytes(candidate));
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("test/contracts/honcho-v3-wire.json not found above " + AppContext.BaseDirectory);
    }
}