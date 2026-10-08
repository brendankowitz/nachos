using System.Text.Json;

namespace Nachos.Client.Tests;

/// <summary>Reads the routes of <c>test/contracts/honcho-v3-wire.json</c> by walking up from the test assembly.</summary>
internal static class WireManifest
{
    /// <summary>Nachos extension routes (spec §9.4) that the Honcho manifest cannot contain.</summary>
    public static readonly IReadOnlySet<(string Method, string Path)> ExtensionRoutes =
        new HashSet<(string, string)> { ("POST", "/v3/admin/grants") };

    private static readonly Lazy<IReadOnlyList<(string Method, string Path)>> LazyRoutes = new(Load);

    public static IReadOnlyList<(string Method, string Path)> Routes => LazyRoutes.Value;

    public static bool Contains(HttpMethod method, string path) =>
        Routes.Contains((method.Method, path)) || ExtensionRoutes.Contains((method.Method, path));

    private static (string Method, string Path)[] Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "test", "contracts", "honcho-v3-wire.json");
            if (File.Exists(candidate))
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(candidate));
                return document.RootElement.GetProperty("routes").EnumerateArray()
                    .Select(r => (r.GetProperty("method").GetString()!, r.GetProperty("path").GetString()!))
                    .ToArray();
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("test/contracts/honcho-v3-wire.json not found above " + AppContext.BaseDirectory);
    }
}
