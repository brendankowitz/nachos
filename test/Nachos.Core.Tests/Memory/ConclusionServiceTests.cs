using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Core.Tests.Deriver;
using Shouldly;

namespace Nachos.Core.Tests.Memory;

public sealed class ConclusionServiceTests
{
    private const string W = MemoryFixture.W;

    private static ConclusionCreate Create(string content = "alice likes tea", string? session = "S1") =>
        new(content, "bob", "alice", session);

    private static async Task<ValidationError> Rejected(Func<Task> call) =>
        (await Should.ThrowAsync<RequestValidationException>(call)).Errors.ShouldHaveSingleItem();

    [Fact]
    public async Task CreateBatch_1to100()
    {
        using var f = await new MemoryFixture().SeedAsync();

        var empty = await Rejected(() => f.Service.CreateConclusionsAsync(W, []));
        empty.Loc.ShouldBe(["body", "conclusions"]);
        empty.Type.ShouldBe("too_short");
        var tooMany = await Rejected(() => f.Service.CreateConclusionsAsync(W, [.. Enumerable.Repeat(Create(), 101)]));
        tooMany.Loc.ShouldBe(["body", "conclusions"]);
        tooMany.Type.ShouldBe("too_long");
        (await f.Service.ListConclusionsAsync(W, null, new())).Total.ShouldBe(0);

        var created = await f.Service.CreateConclusionsAsync(W, [.. Enumerable.Range(0, 100).Select(i => Create($"fact {i}"))]);

        created.Count.ShouldBe(100);
        created.ShouldAllBe(c => c.Level == "explicit" && c.SourceIds.Count == 0 && c.TimesDerived == 1
            && c.ObserverId == "bob" && c.ObservedId == "alice" && c.SessionId == "S1");
        created.Select(c => c.Content).ShouldBe(Enumerable.Range(0, 100).Select(i => $"fact {i}"));
        (await f.Service.ListConclusionsAsync(W, null, new(Size: 100))).Items.Select(c => c.Id).ShouldBe(created.Select(c => c.Id));
    }

    [Fact]
    public async Task Create_IsNotDeduplicated_AndMayOmitTheSession()
    {
        using var f = await new MemoryFixture().SeedAsync();

        var created = await f.Service.CreateConclusionsAsync(W, [Create(session: null), Create(session: null)]);

        created.Select(c => c.Id).Distinct().Count().ShouldBe(2);
        created.ShouldAllBe(c => c.SessionId == null);
    }

    [Fact]
    public async Task Create_ContentOver65535_422()
    {
        using var f = await new MemoryFixture().SeedAsync();

        var tooLong = await Rejected(() => f.Service.CreateConclusionsAsync(W, [Create(), Create(new string('x', 65536))]));
        tooLong.Loc.ShouldBe(["body", "conclusions", 1, "content"]);
        tooLong.Type.ShouldBe("string_too_long");
        var empty = await Rejected(() => f.Service.CreateConclusionsAsync(W, [Create("")]));
        empty.Loc.ShouldBe(["body", "conclusions", 0, "content"]);
        empty.Type.ShouldBe("string_too_short");
        var missing = await Rejected(() => f.Service.CreateConclusionsAsync(W, [Create(null!)]));
        missing.Type.ShouldBe("string_type");
        (await Rejected(() => f.Service.CreateConclusionsAsync(W, [null!]))).Loc.ShouldBe(["body", "conclusions", 0]);
        (await f.Service.ListConclusionsAsync(W, null, new())).Total.ShouldBe(0);

        // Lengths count Unicode characters: 65,535 astral characters (131,070 UTF-16 units) fit.
        var astral = string.Concat(Enumerable.Repeat("\U0001F600", 65535));
        (await f.Service.CreateConclusionsAsync(W, [Create(new string('x', 65535)), Create(astral)])).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Create_InvalidIds_AreRejected_BeforeAnyWrite()
    {
        using var f = await new MemoryFixture().SeedAsync();

        await Should.ThrowAsync<NachosValidationException>(() => f.Service.CreateConclusionsAsync(W, [new("x", "bad id", "alice")]));
        await Should.ThrowAsync<NachosValidationException>(() => f.Service.CreateConclusionsAsync(W, [new("x", "bob", "alice", "bad id")]));
        await Should.ThrowAsync<NotFoundException>(() => f.Service.CreateConclusionsAsync(W, [new("x", "bob", "nobody")]));
        (await f.Service.ListConclusionsAsync(W, null, new())).Total.ShouldBe(0);
    }

    [Fact]
    public async Task Create_EmbedsInline_SoAQueryFindsItAtOnce()
    {
        using var f = await new MemoryFixture().SeedAsync();

        var created = (await f.Service.CreateConclusionsAsync(W, [Create("alice likes tea")])).Single();

        (await f.Service.QueryConclusionsAsync(W, new("alice likes tea"))).Single().Id.ShouldBe(created.Id);
        (await f.Store.Embeddings.ListPendingAsync(10, default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_WhenEmbeddingFails_StillSucceeds_AndLeavesTheConclusionPending()
    {
        using var f = await new MemoryFixture().SeedAsync();
        f.Embeddings.FailNext = true;

        var created = (await f.Service.CreateConclusionsAsync(W, [Create("alice likes tea")])).Single();

        (await f.Service.GetConclusionAsync(W, created.Id)).Content.ShouldBe("alice likes tea");
        (await f.Store.Embeddings.ListPendingAsync(10, default)).Single().ConclusionId.ShouldBe(created.Id);
        (await f.Service.QueryConclusionsAsync(W, new("alice likes tea"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Query_TopKAndDistance()
    {
        using var f = await new MemoryFixture().SeedAsync();
        f.Embeddings.Vectors["near"] = FlakyEmbeddingGenerator.Axis(0);
        f.Embeddings.Vectors["nearer"] = FlakyEmbeddingGenerator.Axis(0, tilt: 0.05f);
        f.Embeddings.Vectors["far"] = FlakyEmbeddingGenerator.Axis(2);
        f.Embeddings.Vectors["query"] = FlakyEmbeddingGenerator.Axis(0, tilt: 0.04f);
        await f.Service.CreateConclusionsAsync(W, [Create("far"), Create("near"), Create("nearer")]);

        (await f.Service.QueryConclusionsAsync(W, new("query"))).Select(c => c.Content).ShouldBe(["nearer", "near", "far"]);
        (await f.Service.QueryConclusionsAsync(W, new("query", TopK: 2))).Select(c => c.Content).ShouldBe(["nearer", "near"]);
        (await f.Service.QueryConclusionsAsync(W, new("query", Distance: 0.01))).Select(c => c.Content).ShouldBe(["nearer", "near"]);
        (await f.Service.QueryConclusionsAsync(W, new("query", Distance: 0))).ShouldBeEmpty();
        (await f.Service.QueryConclusionsAsync(W, new("query", Filters: new JsonObject { ["session_id"] = "S2" }))).ShouldBeEmpty();

        foreach (var (topK, type) in new[] { (0, "greater_than_equal"), (101, "less_than_equal") })
        {
            var error = await Rejected(() => f.Service.QueryConclusionsAsync(W, new("query", TopK: topK)));
            error.Loc.ShouldBe(["body", "top_k"]);
            error.Type.ShouldBe(type);
        }

        foreach (var (distance, type) in new[] { (-0.01, "greater_than_equal"), (1.01, "less_than_equal"), (double.NaN, "finite_number") })
        {
            var error = await Rejected(() => f.Service.QueryConclusionsAsync(W, new("query", Distance: distance)));
            error.Loc.ShouldBe(["body", "distance"]);
            error.Type.ShouldBe(type);
        }

        (await f.Service.QueryConclusionsAsync(W, new("query", TopK: 100, Distance: 1))).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Delete_TombstonesAndHides()
    {
        using var f = await new MemoryFixture().SeedAsync();
        var created = await f.Service.CreateConclusionsAsync(W, [Create("kept"), Create("deleted")]);

        await f.Service.DeleteConclusionAsync(W, created[1].Id);

        await Should.ThrowAsync<NotFoundException>(() => f.Service.GetConclusionAsync(W, created[1].Id));
        (await f.Service.ListConclusionsAsync(W, null, new())).Items.Select(c => c.Id).ShouldBe([created[0].Id]);
        (await f.Service.QueryConclusionsAsync(W, new("deleted"))).Select(c => c.Id).ShouldBe([created[0].Id]);
        await f.Service.DeleteConclusionAsync(W, created[1].Id);
        await f.Service.DeleteConclusionAsync(W, "missing");
        (await f.Service.GetConclusionAsync(W, created[0].Id)).Id.ShouldBe(created[0].Id);
    }

    [Fact]
    public async Task List_FiltersAndPages_InCreationOrder()
    {
        using var f = await new MemoryFixture().SeedAsync();
        var created = await f.Service.CreateConclusionsAsync(W, [Create("a"), Create("b", "S2"), Create("c")]);

        (await f.Service.ListConclusionsAsync(W, new JsonObject { ["session_id"] = "S1" }, new())).Items
            .Select(c => c.Content).ShouldBe(["a", "c"]);
        var reversed = await f.Service.ListConclusionsAsync(W, null, new(Size: 2, Reverse: true));
        reversed.Items.Select(c => c.Content).ShouldBe(["c", "b"]);
        reversed.Total.ShouldBe(3);
        reversed.Pages.ShouldBe(2);
        (await f.Service.ListConclusionsAsync(W, new JsonObject { ["id"] = created[1].Id }, new())).Items
            .ShouldHaveSingleItem().Content.ShouldBe("b");
    }
}
