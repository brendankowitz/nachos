using System.Text.RegularExpressions;

namespace Nachos.LicenseCheck;

internal static class PythonLock
{
    private static readonly Regex Requirement = new(
        """\A(?<name>[A-Za-z0-9][A-Za-z0-9._-]*)==(?<version>[A-Za-z0-9][A-Za-z0-9.!+_-]*)(?:[ \t]*;[ \t]*sys_platform[ \t]*==[ \t]*(?:'win32'|"win32"))?(?<hashes>(?:[ \t]+--hash=sha256:[a-fA-F0-9]{64})+)\z""",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static Dictionary<string, (string Name, string Version, string[] Hashes)> Read(string path)
    {
        var requirements = new Dictionary<string, (string Name, string Version, string[] Hashes)>(StringComparer.Ordinal);
        var pending = "";
        var continued = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            continued = line.EndsWith('\\');
            pending += (pending.Length == 0 ? "" : " ") + (continued ? line[..^1].TrimEnd() : line);
            if (continued) continue;

            var match = Requirement.Match(pending);
            if (!match.Success)
            {
                throw new InvalidDataException("Unsupported or unpinned Python requirement: expected name==version, "
                    + "only an optional sys_platform == 'win32' marker, and one or more SHA256 hashes: " + pending);
            }
            var name = match.Groups["name"].Value;
            var version = match.Groups["version"].Value;
            var hashes = Regex.Matches(match.Groups["hashes"].Value, @"--hash=sha256:([a-fA-F0-9]{64})")
                .Select(hash => hash.Groups[1].Value).ToArray();
            if (!requirements.TryAdd(PackageEvidence.Identity("python", name, version), (name, version, hashes)))
                throw new InvalidDataException($"Duplicate Python requirement identity: {name}@{version}");
            pending = "";
        }
        if (continued) throw new InvalidDataException("Unsupported Python requirement: unfinished line continuation.");
        if (requirements.Count == 0) throw new InvalidDataException("Python requirements.lock is empty.");
        return requirements;
    }

    // Audit every identity regardless of the installation marker's host applicability.
    public static string Project(string path) => string.Concat(Read(path).Values.Select(requirement =>
        $"{requirement.Name}=={requirement.Version}"
        + string.Concat(requirement.Hashes.Select(hash => $" --hash=sha256:{hash}")) + "\n"));
}
