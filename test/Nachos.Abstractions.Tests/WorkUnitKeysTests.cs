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
        RepresentationPayload.ReadObservers(payload).ShouldBe(["alice", "bob"]);
        Should.Throw<FormatException>(() => RepresentationPayload.ReadObservers(new JsonObject()));
        Should.Throw<FormatException>(() => RepresentationPayload.ReadObservers(new JsonObject { ["observers"] = new JsonArray(1) }));
    }
}
