using System.Text.Json.Nodes;
using Nachos.Abstractions.Work;
using Shouldly;
using Xunit;

namespace Nachos.Abstractions.Tests;

public sealed class WorkUnitKeysTests
{
    [Fact]
    public void Representation_KeepsShortApiNamesVerbatim()
    {
        WorkUnitKeys.Representation("ws_1", "session-A", "bob").ShouldBe("representation:ws_1:session-A:bob");

        var longest = new string('a', WorkUnitKeys.MaxComponentLength);
        WorkUnitKeys.Representation(longest, longest, longest).ShouldBe($"representation:{longest}:{longest}:{longest}");
    }

    [Fact]
    public void Representation_HashesOverlongComponents_AndStaysWithinTheKeyLimit()
    {
        var name = new string('a', 512);
        var key = WorkUnitKeys.Representation(name, name, name);

        key.Length.ShouldBeLessThanOrEqualTo(WorkUnitKeys.MaxLength);
        key.Split(':').Skip(1).ShouldAllBe(component => component.StartsWith('~') && component.Length == 33);
        WorkUnitKeys.Representation(name, name, name).ShouldBe(key);
        WorkUnitKeys.Representation(name, name, name + "b").ShouldNotBe(key);

        var longestPlain = new string('z', WorkUnitKeys.MaxComponentLength);
        WorkUnitKeys.Representation(longestPlain, longestPlain, longestPlain).Length.ShouldBeLessThanOrEqualTo(WorkUnitKeys.MaxLength);
    }

    [Theory]
    [InlineData("a:b", "c", "a", "b:c")]
    [InlineData("~x", "s", "~x", "s ")]
    [InlineData("ws", "", "ws", "~")]
    public void Representation_DistinctNames_NeverShareAKey(string workspace1, string session1, string workspace2, string session2)
    {
        WorkUnitKeys.Representation(workspace1, session1, "bob")
            .ShouldNotBe(WorkUnitKeys.Representation(workspace2, session2, "bob"));
    }

    [Fact]
    public void Representation_LoneSurrogates_HashByCodeUnit()
    {
        // Built in code: xunit mangles lone surrogates in attribute data.
        var first = new string((char)0xD800, 1);
        var second = new string((char)0xD801, 1);

        WorkUnitKeys.Representation("ws", first, "bob").ShouldNotBe(WorkUnitKeys.Representation("ws", second, "bob"));
    }

    [Fact]
    public void Representation_NamesOutsideTheApiAlphabet_AreHashed()
    {
        WorkUnitKeys.Representation("ws", "a:b", "bob").ShouldNotContain("a:b");
        WorkUnitKeys.Representation("ws", "Ünïcode", "bob").Split(':')[2].ShouldStartWith("~");
    }

    [Fact]
    public void RepresentationPayload_RoundTripsObservers()
    {
        var payload = RepresentationPayload.Create(["alice", "bob"]);

        payload.ToJsonString().ShouldBe("""{"observers":["alice","bob"]}""");
        RepresentationPayload.Read(payload).ShouldSatisfyAllConditions(
            work => work.Observers.ShouldBe(["alice", "bob"]),
            work => work.ReasoningEnabled.ShouldBeNull(),
            work => work.CustomInstructions.ShouldBeNull());
        Should.Throw<FormatException>(() => RepresentationPayload.Read(new JsonObject()));
        Should.Throw<FormatException>(() => RepresentationPayload.Read(new JsonObject { ["observers"] = new JsonArray(1) }));
    }

    [Fact]
    public void RepresentationPayload_RejectsAnEmptyOrNullObserver()
    {
        Should.Throw<ArgumentException>(() => RepresentationPayload.Create(["alice", ""]));
        Should.Throw<ArgumentException>(() => RepresentationPayload.Create([null!]));
    }

    [Fact]
    public void RepresentationPayload_RoundTripsTheMessageReasoningSnapshot()
    {
        var payload = RepresentationPayload.Create(["alice"], reasoningEnabled: false, customInstructions: "Be brief.");

        payload.ToJsonString().ShouldBe("""{"observers":["alice"],"reasoning_enabled":false,"custom_instructions":"Be brief."}""");
        RepresentationPayload.Read(payload).ShouldSatisfyAllConditions(
            work => work.Observers.ShouldBe(["alice"]),
            work => work.ReasoningEnabled.ShouldBe(false),
            work => work.CustomInstructions.ShouldBe("Be brief."));
        RepresentationPayload.Read(new JsonObject { ["observers"] = new JsonArray(), ["reasoning_enabled"] = null })
            .ReasoningEnabled.ShouldBeNull();
        Should.Throw<FormatException>(() => RepresentationPayload.Read(
            new JsonObject { ["observers"] = new JsonArray(), ["reasoning_enabled"] = "yes" }));
        Should.Throw<FormatException>(() => RepresentationPayload.Read(
            new JsonObject { ["observers"] = new JsonArray(), ["custom_instructions"] = 3 }));
    }
}
