using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nachos.Abstractions.Filtering;

namespace Nachos.Testing.Filtering;

/// <summary>One shared filter case: either the ids it must return or that it must be rejected.</summary>
/// <param name="Name">Unique case name.</param>
/// <param name="Resource">The resource the filter runs against.</param>
/// <param name="Filter">The filter JSON as a client would send it.</param>
/// <param name="Expect">The expected result ids (names, or message public ids); null for an error case.</param>
/// <param name="Error">True when the parser must reject the filter with a validation error.</param>
public sealed record FilterCase(
    string Name, ResourceKind Resource, JsonNode? Filter, IReadOnlyList<string>? Expect, bool Error);

/// <summary>Loads the embedded <c>filter-cases.json</c> shared by the parser tests and every provider.</summary>
public static class FilterCaseLibrary
{
    private const string ResourceName = "Nachos.Testing.Filtering.filter-cases.json";

    private static readonly Lazy<CaseFile> File = new(Load);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The dataset the cases run against.</summary>
    public static FilterDataset Dataset => File.Value.Dataset;

    /// <summary>Every case, in file order.</summary>
    public static IReadOnlyList<FilterCase> All => File.Value.Cases;

    /// <summary>Looks a case up by its unique name.</summary>
    public static FilterCase Get(string name) => All.Single(c => c.Name == name);

    /// <summary>The case names as xunit <c>MemberData</c> rows.</summary>
    public static IEnumerable<object[]> CaseNameData() => All.Select(c => new object[] { c.Name });

    private static CaseFile Load()
    {
        using var stream = typeof(FilterCaseLibrary).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        return JsonSerializer.Deserialize<CaseFile>(stream, Options)
            ?? throw new InvalidOperationException("filter-cases.json is empty.");
    }

    private sealed record CaseFile(FilterDataset Dataset, IReadOnlyList<FilterCase> Cases);
}
