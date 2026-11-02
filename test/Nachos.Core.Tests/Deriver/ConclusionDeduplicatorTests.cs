using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Core.Configuration;
using Nachos.Core.Deriver;
using Nachos.DataLayer.InMemory;
using Shouldly;

namespace Nachos.Core.Tests.Deriver;

public sealed class ConclusionDeduplicatorTests
{
    private const string Workspace = "W";
    private const string Session = "S";
    private static CancellationToken Ct => CancellationToken.None;

    private readonly InMemoryMemoryStore _store = new InMemoryMemoryStore(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
    private readonly ConclusionDeduplicator _deduplicator;

    public ConclusionDeduplicatorTests()
    {
        _deduplicator = new ConclusionDeduplicator(_store, Options.Create(new NachosOptions()));
    }

    /// <summary>A unit vector along <paramref name="axis"/>, tilted towards axis 1 by <paramref name="tilt"/>.</summary>
    private static ReadOnlyMemory<float> Vector(int axis, float tilt = 0)
    {
        var vector = new float[VectorSpace.Dimensions];
        vector[axis] = 1;
        vector[1] += tilt;
        return vector;
    }

    private static DerivedFact Fact(string content, ReadOnlyMemory<float> embedding, params string[] sources) =>
        new(content, embedding, sources.Length == 0 ? ["m1"] : sources);

    private async Task SeedAsync(params string[] sessions)
    {
        await _store.Workspaces.GetOrCreateAsync(Workspace, null, null, Ct);
        foreach (var session in sessions.Append(Session).Distinct())
        {
            await _store.Sessions.GetOrCreateAsync(
                Workspace, session, null, null, new Dictionary<string, SessionPeerConfig> { ["alice"] = new(), ["bob"] = new() }, Ct);
        }
    }

    private async Task<ConclusionRecord> ExistingAsync(string content, ReadOnlyMemory<float> embedding, string session = Session) =>
        (await _store.Conclusions.InsertAsync(
            Workspace, [new NewConclusion("bob", "alice", session, ConclusionLevel.Explicit, content, embedding, [], null)], Ct)).Single();

    private Task<DeduplicationResult> DeduplicateAsync(params DerivedFact[] facts) =>
        _deduplicator.DeduplicateAsync(Workspace, "bob", "alice", Session, facts, Ct);

    [Fact]
    public async Task ExactDuplicate_Corroborates()
    {
        await SeedAsync();
        // A different vector proves the exact lookup decides on its own, before any semantic match.
        var existing = await ExistingAsync("Alice lives in Paris.", Vector(0));

        var result = await DeduplicateAsync(Fact("Alice lives in Paris.", Vector(2)));

        result.Corroborations.ShouldBe([existing.PublicId]);
        result.Inserts.ShouldBeEmpty();
        result.Replacements.ShouldBeEmpty();
    }

    [Fact]
    public async Task SemanticDuplicateWithinThreshold_Corroborates()
    {
        await SeedAsync();
        var existing = await ExistingAsync("Alice enjoys drinking green tea.", Vector(0));

        var result = await DeduplicateAsync(Fact("Alice likes green tea.", Vector(0, tilt: 0.1f)));

        result.Corroborations.ShouldBe([existing.PublicId]);
        result.Inserts.ShouldBeEmpty();
        result.Replacements.ShouldBeEmpty();
    }

    [Fact]
    public async Task SemanticNeighbourBeyondThreshold_Inserts()
    {
        await SeedAsync();
        await ExistingAsync("Alice likes tea.", Vector(0));

        var result = await DeduplicateAsync(Fact("Alice owns a bicycle.", Vector(0, tilt: 0.5f)));

        result.Inserts.Select(f => f.Content).ShouldBe(["Alice owns a bicycle."]);
        result.Corroborations.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExplicitFact_OtherSession_IsNotDuplicate()
    {
        await SeedAsync("other");
        await ExistingAsync("Alice lives in Paris.", Vector(0), session: "other");

        var result = await DeduplicateAsync(Fact("Alice lives in Paris.", Vector(0)));

        result.Inserts.ShouldHaveSingleItem().Content.ShouldBe("Alice lives in Paris.");
        result.Corroborations.ShouldBeEmpty();
        result.Replacements.ShouldBeEmpty();
    }

    [Fact]
    public async Task NonExplicitNeighbour_IsNotADuplicate()
    {
        await SeedAsync();
        await _store.Conclusions.InsertAsync(
            Workspace,
            [new NewConclusion("bob", "alice", Session, ConclusionLevel.Deductive, "Alice likes tea.", Vector(0), [], null)],
            Ct);

        var result = await DeduplicateAsync(Fact("Alice likes tea.", Vector(0)));

        result.Inserts.ShouldHaveSingleItem();
        result.Corroborations.ShouldBeEmpty();
        result.Replacements.ShouldBeEmpty();
    }

    [Fact]
    public async Task RicherFact_Replaces()
    {
        await SeedAsync();
        var existing = await ExistingAsync("Alice likes tea.", Vector(0));
        var richer = Vector(0, tilt: 0.1f);

        var result = await DeduplicateAsync(Fact("Alice likes green tea in the morning.", richer));

        var replacement = result.Replacements.ShouldHaveSingleItem();
        replacement.ConclusionId.ShouldBe(existing.PublicId);
        replacement.Content.ShouldBe("Alice likes green tea in the morning.");
        replacement.Embedding!.Value.ToArray().ShouldBe(richer.ToArray());
        result.Corroborations.ShouldBeEmpty();
        result.Inserts.ShouldBeEmpty();
    }

    [Fact]
    public async Task LongerFactMissingAnOldToken_IsNotRicher()
    {
        await SeedAsync();
        var existing = await ExistingAsync("Alice likes black tea.", Vector(0));

        var result = await DeduplicateAsync(Fact("Alice likes green tea very much.", Vector(0, tilt: 0.1f)));

        result.Corroborations.ShouldBe([existing.PublicId]);
        result.Replacements.ShouldBeEmpty();
    }

    [Fact]
    public async Task TwoFactsMatchingOneConclusion_CountItOnce()
    {
        await SeedAsync();
        var existing = await ExistingAsync("Alice likes tea.", Vector(0));

        var result = await DeduplicateAsync(
            Fact("Alice likes tea.", Vector(0), "m1"),
            Fact("Alice likes green tea.", Vector(0, tilt: 0.1f), "m2"),
            Fact("Alice likes tea.", Vector(0), "m3"));

        result.Corroborations.ShouldBeEmpty();
        result.Replacements.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            r => r.ConclusionId.ShouldBe(existing.PublicId),
            r => r.Content.ShouldBe("Alice likes green tea."));
        result.Inserts.ShouldBeEmpty();
    }

    [Fact]
    public async Task WithinBatchDuplicates_InsertOnce_WithMergedSources()
    {
        await SeedAsync();

        var result = await DeduplicateAsync(
            Fact("Alice has a dog.", Vector(3), "m1"),
            Fact("Alice has a dog.", Vector(3), "m2"),
            Fact("Alice has a dog named Rex.", Vector(3, tilt: 0.1f), "m3"),
            Fact("Alice speaks French.", Vector(4), "m4"));

        result.Inserts.Count.ShouldBe(2);
        var dog = result.Inserts[0];
        dog.Content.ShouldBe("Alice has a dog named Rex.");
        dog.SourceMessageIds.ShouldBe(["m1", "m2", "m3"]);
        result.Inserts[1].Content.ShouldBe("Alice speaks French.");
        result.Corroborations.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("  Alice   lives\tin\r\nParis.  ", "Alice lives in Paris.")]
    [InlineData("Café", "Café")]
    [InlineData(" \t ", "")]
    public void Normalize_TrimsCollapsesWhitespaceAndComposes(string input, string expected) =>
        ConclusionDeduplicator.Normalize(input).ShouldBe(expected);
}
