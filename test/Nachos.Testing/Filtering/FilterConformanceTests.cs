using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Filtering;
using Nachos.Testing.Json;
using Shouldly;
using Xunit;

namespace Nachos.Testing.Filtering;

/// <summary>
/// Behavioural contract for a provider's filter compiler. A provider's test project derives from this class,
/// builds <see cref="FilterDataset"/> into its store, and runs filters against it; the cases (see
/// <c>filter-cases.json</c>) pin the semantics documented on <see cref="FilterNode"/> and <see cref="FilterOp"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SeedAsync"/> runs <b>once per derived class</b> (the first case to start triggers it and every other
/// case waits for it), not once per test instance. It therefore must not depend on state owned by one test instance
/// (use static or fixture-owned resources), and it must be <b>idempotent</b>: if the dataset's first workspace
/// already exists, do nothing. Rows outside the dataset are ignored, so a shared database is tolerated.
/// </para>
/// <para>
/// The public store interfaces cannot create the dataset exactly, so a provider seeds through a private path with
/// these operations: set <c>CreatedAt</c> per workspace, peer and session row; set a session's
/// <c>LifecycleState = Inactive</c> where <c>IsActive</c> is false; insert messages with the given public ids (and
/// <c>CreatedAt</c>, token count and metadata), in dataset order within a session so <c>Seq</c> follows it; and
/// insert memberships with <c>LeftAt</c> set for members whose <c>Active</c> is false. For SQL, direct inserts are
/// fine.
/// </para>
/// </remarks>
public abstract class FilterConformanceTests : IAsyncLifetime
{
    private static readonly ConcurrentDictionary<Type, Lazy<Task>> Seeded = new();

    public static IEnumerable<object[]> CaseNames => FilterCaseLibrary.CaseNameData();

    public Task InitializeAsync() =>
        Seeded.GetOrAdd(GetType(), _ => new Lazy<Task>(() => SeedAsync(FilterCaseLibrary.Dataset))).Value;

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Stores the dataset exactly as given, once per derived class, idempotently. See the class remarks for the
    /// operations this needs.
    /// </summary>
    protected abstract Task SeedAsync(FilterDataset dataset);

    /// <summary>
    /// Lists <b>all</b> rows of <paramref name="kind"/> matching <paramref name="filter"/> (null means no filter) and
    /// returns their ids: names for workspaces, peers (<c>PeerKind.All</c>) and sessions of the scope workspace, and
    /// public ids for messages. Messages are listed per session, so run the filter against every dataset session
    /// and return the union. Implementations page through the whole result.
    /// </summary>
    protected abstract Task<IReadOnlyList<string>> QueryAsync(ResourceKind kind, FilterNode? filter);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Case(string name)
    {
        var filterCase = FilterCaseLibrary.Get(name);

        if (filterCase.Error)
        {
            Should.Throw<NachosValidationException>(
                () => FilterParser.Parse(filterCase.Filter, filterCase.Resource));
            return;
        }

        var filter = FilterParser.Parse(filterCase.Filter, filterCase.Resource);
        var returned = await QueryAsync(filterCase.Resource, filter);

        var datasetIds = DatasetIds(FilterCaseLibrary.Dataset, filterCase.Resource);
        var inDataset = returned.Where(datasetIds.Contains).ToList();
        inDataset.ShouldBe(filterCase.Expect!, ignoreOrder: true);
        inDataset.Distinct().Count().ShouldBe(inDataset.Count, "duplicate ids");
    }

    /// <summary>
    /// A filter node built by hand, with an operand that carries a caller converter, must evaluate like the parsed one:
    /// the node itself stores a canonical copy, so no provider ever runs the converter (for instance while it holds a lock).
    /// </summary>
    [Fact]
    public async Task HandBuiltOperandWithAConverter_EvaluatesLikeTheParsedFilter_AndNeverRunsTheConverter()
    {
        var (filterCase, parsed, literal) = EqualityOnAStringInMetadata();
        var path = ((FilterNode.MetadataPath)parsed).Path;
        var converter = new CountingUppercaseConverter();
        var handBuilt = new FilterNode.MetadataPath(
            path, FilterOp.Eq, StrictJsonSamples.UppercasedString(literal, converter));

        var expected = await QueryAsync(filterCase.Resource, parsed);
        var actual = await QueryAsync(filterCase.Resource, handBuilt);

        actual.ShouldBe(expected, ignoreOrder: true);
        converter.Calls.ShouldBe(0);
    }

    /// <summary>
    /// The operand a node returns is a detached copy, so a caller cannot put a converter into the stored one by
    /// changing what <c>Value</c> returned: the next evaluation, comparison and printout still run no caller code.
    /// </summary>
    [Fact]
    public async Task ChangingTheOperandThatAHandBuiltNodeReturned_DoesNotChangeTheNode_OrRunAConverter()
    {
        var (filterCase, parsed) = FilterCaseLibrary.All
            .Where(c => !c.Error && c.Expect is { Count: > 0 })
            .Select(c => (Case: c, Parsed: FilterParser.Parse(c.Filter, c.Resource)!))
            .First(x => x.Parsed is FilterNode.MetadataPath { Value: JsonArray or JsonObject });
        var source = (FilterNode.MetadataPath)parsed;
        var converter = new CountingUppercaseConverter();
        var handBuilt = new FilterNode.MetadataPath(source.Path, source.Op, source.Value);

        switch (handBuilt.Value)
        {
            case JsonArray array:
                array.Add(StrictJsonSamples.UppercasedString("x", converter));
                break;
            case JsonObject obj:
                obj["injected"] = StrictJsonSamples.UppercasedString("x", converter);
                break;
        }

        var expected = await QueryAsync(filterCase.Resource, parsed);
        var actual = await QueryAsync(filterCase.Resource, handBuilt);

        actual.ShouldBe(expected, ignoreOrder: true);
        handBuilt.Equals(parsed).ShouldBeTrue();
        handBuilt.ToString().ShouldNotContain("injected");
        converter.Calls.ShouldBe(0);
    }
    /// <summary>
    /// Metadata is stored canonically, so a <see cref="DateTimeOffset"/> operand on a metadata path is its ISO string:
    /// it evaluates exactly like the string-built node and does not throw.
    /// </summary>
    [Fact]
    public async Task HandBuiltMetadataPathWithADateTimeOffset_EvaluatesLikeItsIsoString()
    {
        var instant = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var typed = new FilterNode.MetadataPath(["when"], FilterOp.Eq, JsonValue.Create(instant));
        var text = new FilterNode.MetadataPath(["when"], FilterOp.Eq, JsonValue.Create("2026-01-02T03:04:05+00:00"));

        var viaTyped = await QueryAsync(ResourceKind.Workspace, typed);
        var viaText = await QueryAsync(ResourceKind.Workspace, text);

        typed.Value!.GetValue<string>().ShouldBe(text.Value!.GetValue<string>());
        viaTyped.ShouldBe(viaText, ignoreOrder: true);
    }

    // The first equality case on a string in metadata that has matches, whose literal changes under upper-casing.
    private static (FilterCase Case, FilterNode Parsed, string Literal) EqualityOnAStringInMetadata() =>
        FilterCaseLibrary.All
            .Where(c => !c.Error && c.Expect is { Count: > 0 })
            .Select(c => (Case: c, Parsed: FilterParser.Parse(c.Filter, c.Resource)!))
            .Select(x => (x.Case, x.Parsed, Literal: (x.Parsed as FilterNode.MetadataPath)?.Value as JsonValue))
            .Where(x => x.Parsed is FilterNode.MetadataPath { Op: FilterOp.Eq }
                && x.Literal is not null
                && x.Literal.GetValueKind() == JsonValueKind.String
                && x.Literal.GetValue<string>().Any(char.IsLower))
            .Select(x => (x.Case, x.Parsed, x.Literal!.GetValue<string>()))
            .First();

    private static HashSet<string> DatasetIds(FilterDataset dataset, ResourceKind kind) => (kind switch
    {
        ResourceKind.Workspace => dataset.Workspaces.Select(w => w.Name),
        ResourceKind.Peer => dataset.Peers.Select(p => p.Name),
        ResourceKind.Session => dataset.Sessions.Select(s => s.Name),
        _ => dataset.Messages.Select(m => m.Id),
    }).ToHashSet(StringComparer.Ordinal);
}
