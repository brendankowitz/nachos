using System.Security.Cryptography;
using System.Reflection;
using System.Text;

namespace Nachos.DocsGen;

internal sealed class Inputs
{
    internal const string DefaultOutput = "docs/site/src/content/docs/reference/_generated";
    internal string Root { get; }
    internal string Output { get; }
    internal string Configuration { get; }
    internal string OpenApi { get; }
    internal string Cli { get; }
    internal string Manifest { get; }
    internal SortedDictionary<string, string> Evidence { get; } = new(StringComparer.Ordinal);
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    private Inputs(Dictionary<string, string> args)
    {
        Root = Path.GetFullPath(args.GetValueOrDefault("--root", Directory.GetCurrentDirectory()));
        if (!Directory.Exists(Root)) throw new InvalidDataException();
        Configuration = args.GetValueOrDefault("--configuration")
            ?? typeof(Inputs).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
            ?? throw new InvalidDataException();
        if (Configuration is not ("Debug" or "Release")) throw new InvalidDataException();
        Output = Resolve(args.GetValueOrDefault("--output", DefaultOutput));
        OpenApi = Resolve(args.GetValueOrDefault("--openapi", "src/Nachos.Api/obj/Nachos.Api.json"));
        Cli = Resolve(args.GetValueOrDefault("--cli", $"src/Nachos.Cli/bin/{Configuration}/net10.0/Nachos.Cli.dll"));
        Manifest = Resolve(args.GetValueOrDefault("--manifest", "test/contracts/honcho-v3-wire.json"));
        // The destination is a dedicated generated directory, never a source/input ancestor.
        if (Output != Resolve(DefaultOutput) &&
            !Output.StartsWith(Resolve("test/Nachos.DocsGen.Tests/obj") + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidDataException();
    }

    internal static Inputs Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length ||
                args[i] is not ("--root" or "--configuration" or "--output" or "--openapi" or "--cli" or "--manifest") ||
                string.IsNullOrWhiteSpace(args[i + 1]) || !values.TryAdd(args[i], args[i + 1]))
                throw new InvalidDataException();
        }
        return new Inputs(values);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal string Resolve(string path)
    {
        var full = Path.GetFullPath(path, Root);
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidDataException();
        RejectLinks(full);
        return full;
    }

    internal static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException();
        }
    }

    internal byte[] Read(string path)
    {
        path = Resolve(path);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > 32 * 1024 * 1024) throw new InvalidDataException();
        var bytes = File.ReadAllBytes(path);
        Evidence[Path.GetRelativePath(Root, path).Replace('\\', '/')] = Hash(bytes);
        return bytes;
    }

    internal void VerifyUnchanged()
    {
        foreach (var (path, hash) in Evidence)
        {
            var full = Resolve(path);
            if (Hash(File.ReadAllBytes(full)) != hash) throw new InvalidDataException();
        }
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
