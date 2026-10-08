using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nachos.LicenseCheck;

internal sealed class NpmOverrides
{
    private const string ExactVersion = @"\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?(?:\+[A-Za-z0-9.-]+)?";
    private readonly NpmOverrides? parent;
    private readonly string? key;
    private readonly string? name;
    private readonly string? version;
    private readonly string? replacement;
    private readonly List<NpmOverrides> children = [];

    private NpmOverrides(JsonElement value, string? key = null, NpmOverrides? parent = null)
    {
        this.key = key;
        this.parent = parent;
        if (key is not null)
        {
            var match = Regex.Match(key, @"^(?<name>(?:@[a-z0-9._-]+/)?[a-z0-9][a-z0-9._-]*)(?:@(?<version>" + ExactVersion + "))?$");
            if (!match.Success)
            {
                throw new InvalidDataException($"Unsupported npm override selector: {key}");
            }
            name = match.Groups["name"].Value;
            version = match.Groups["version"].Success ? match.Groups["version"].Value : null;
        }
        if (key is null && value.ValueKind == JsonValueKind.Undefined)
        {
            return;
        }
        if (key is not null && value.ValueKind == JsonValueKind.String && IsExactVersion(value.GetString()!))
        {
            replacement = value.GetString();
            return;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Unsupported npm override value: {key ?? "root"}; exact versions or nested scopes are required.");
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in value.EnumerateObject())
        {
            if (!keys.Add(child.Name))
            {
                throw new InvalidDataException($"Duplicate npm override selector: {child.Name}");
            }
            children.Add(new NpmOverrides(child.Value, child.Name, this));
        }
    }

    public static NpmOverrides Read(JsonElement manifest) =>
        new(manifest.TryGetProperty("overrides", out var value) ? value : default);

    public static bool IsExactVersion(string value) => Regex.IsMatch(value, "^" + ExactVersion + "$");

    public (NpmOverrides Context, string EffectiveRequest) ForDependency(string dependency, string request)
    {
        // npm's nearest ruleset wins, including its own rule, then inherited
        // scopes. Context follows dependency edges, never node_modules placement.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var scope = this; scope is not null; scope = scope.parent)
        {
            foreach (var rule in scope.children.Append(scope))
            {
                if (rule.key is null || !seen.Add(rule.key) || rule.name != dependency)
                {
                    continue;
                }
                if (rule.version is not null)
                {
                    if (IsExactVersion(request))
                    {
                        if (request != rule.version)
                        {
                            continue;
                        }
                    }
                    else if (request != "~" + rule.version)
                    {
                        throw new InvalidDataException($"Unsupported npm override selector intersection: {rule.key} against {request}.");
                    }
                }
                return (rule, rule.replacement ?? rule.version ?? request);
            }
        }
        return (this, request);
    }
}
