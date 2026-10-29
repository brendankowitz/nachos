using System.Reflection;
using System.Text.RegularExpressions;

namespace Nachos.Core.Llm;

/// <summary>
/// A Markdown prompt with <c>{{placeholder}}</c> slots and an id and version for telemetry and audit (spec 11.11).
/// A document starts with a front matter block of <c>id:</c> and <c>version:</c> lines between <c>---</c> fences.
/// </summary>
public sealed partial class PromptTemplate
{
    private readonly string _body;

    private PromptTemplate(string id, string version, string body, IReadOnlyList<string> placeholders)
    {
        Id = id;
        Version = version;
        _body = body;
        Placeholders = placeholders;
    }

    public string Id { get; }

    public string Version { get; }

    public IReadOnlyList<string> Placeholders { get; }

    public static PromptTemplate Parse(string document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var match = FrontMatter().Match(document);
        if (!match.Success)
        {
            throw new FormatException("A prompt template must start with a '---' front matter block containing 'id:' and 'version:'.");
        }

        var fields = match.Groups["fields"].Value
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
        if (!fields.TryGetValue("id", out var id) || id.Length == 0 ||
            !fields.TryGetValue("version", out var version) || version.Length == 0)
        {
            throw new FormatException("A prompt template's front matter must define non-empty 'id' and 'version'.");
        }

        var body = document[match.Length..];
        var placeholders = Placeholder().Matches(body)
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new PromptTemplate(id, version, body, placeholders);
    }

    public static PromptTemplate LoadEmbedded(Assembly assembly, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded prompt resource '{resourceName}' was not found in {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Substitutes every placeholder in a single pass; substituted text is never re-scanned.</summary>
    public string Render(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var missing = Placeholders.Where(name => !values.ContainsKey(name)).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException(
                $"Prompt '{Id}' v{Version} needs values for: {string.Join(", ", missing)}.", nameof(values));
        }

        return Placeholder().Replace(_body, m => values[m.Groups["name"].Value]);
    }

    [GeneratedRegex(@"\A---\r?\n(?<fields>.*?)\r?\n---\r?\n", RegexOptions.Singleline)]
    private static partial Regex FrontMatter();

    [GeneratedRegex(@"\{\{(?<name>[A-Za-z][A-Za-z0-9_]*)\}\}")]
    private static partial Regex Placeholder();
}
