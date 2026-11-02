using System.Text.Json.Nodes;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Observation;
using Shouldly;
using Xunit;

namespace Nachos.Abstractions.Tests;

public sealed class ObserverRuleTests
{
    private static readonly DateTimeOffset At = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static MemberObservation Member(string name, bool? observeOthers, DateTimeOffset? joinedAt = null, DateTimeOffset? leftAt = null) =>
        new(name, observeOthers, joinedAt ?? At.AddHours(-1), leftAt);

    /// <summary>
    /// Sender settings (peer-level, session-level observe_me) by the observing member's session-level observe_others,
    /// and the observers that result. The member "alice" is active; "bob" sends.
    /// </summary>
    public static TheoryData<bool?, bool?, bool?, string[]> Combinations() => new()
    {
        { null, null, null, ["bob"] },
        { null, null, false, ["bob"] },
        { null, null, true, ["alice", "bob"] },
        { true, null, true, ["alice", "bob"] },
        { null, true, true, ["alice", "bob"] },
        { true, true, true, ["alice", "bob"] },
        { false, null, true, [] },
        { null, false, true, [] },
        { false, true, true, [] },
        { true, false, true, [] },
        { false, false, null, [] },
    };

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Resolve_CombinesObserveMeAndObserveOthers(
        bool? peerObserveMe, bool? sessionObserveMe, bool? aliceObservesOthers, string[] expected)
    {
        var sender = new PeerObservation("bob", peerObserveMe, sessionObserveMe);

        ObserverRule.Resolve(sender, [Member("alice", aliceObservesOthers), Member("bob", true)], At).ShouldBe(expected);
    }

    [Fact]
    public void Resolve_OnlyMembersActiveAtTheMessageTimeObserve()
    {
        var sender = new PeerObservation("bob", null, null);
        MemberObservation[] members =
        [
            Member("joins-later", true, joinedAt: At.AddTicks(1)),
            Member("joined-exactly", true, joinedAt: At),
            Member("left-exactly", true, leftAt: At),
            Member("left-later", true, leftAt: At.AddTicks(1)),
            Member("never-left", true),
        ];

        ObserverRule.Resolve(sender, members, At).ShouldBe(["bob", "joined-exactly", "left-later", "never-left"]);
    }

    [Fact]
    public void Resolve_IsOrdinalSortedAndDistinct()
    {
        var sender = new PeerObservation("b", null, null);

        ObserverRule.Resolve(sender, [Member("a", true), Member("B", true), Member("a", true), Member("b", true)], At)
            .ShouldBe(["B", "a", "b"]);
    }

    [Fact]
    public void PeerObservation_ReadsOnlyABooleanObserveMe()
    {
        var membership = new SessionPeerConfig(ObserveMe: true);

        PeerObservation.From("p", new JsonObject { ["observe_me"] = false }, membership)
            .ShouldBe(new PeerObservation("p", false, true));
        PeerObservation.From("p", new JsonObject { ["observe_me"] = true }, new SessionPeerConfig())
            .ShouldBe(new PeerObservation("p", true, null));
        PeerObservation.From("p", new JsonObject { ["observe_me"] = "false" }, new SessionPeerConfig())
            .PeerObserveMe.ShouldBeNull();
        PeerObservation.From("p", new JsonObject { ["observe_me"] = null }, new SessionPeerConfig())
            .PeerObserveMe.ShouldBeNull();
        PeerObservation.From("p", new JsonObject(), new SessionPeerConfig()).PeerObserveMe.ShouldBeNull();
    }
}
