using System.Text.Json.Nodes;
using Nachos.Abstractions.Domain;
using Nachos.Core.Search;
using Shouldly;

namespace Nachos.Core.Tests.Search;

public sealed class ReciprocalRankFusionTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static MessageRecord Message(string id, int minute = 0, long seq = 1) =>
        new(id, "W", "S", "alice", seq, id, 1, new JsonObject(), Start.AddMinutes(minute));

    private static MessageHit Hit(MessageRecord message, int rank) => new(message, rank);

    private static string[] Ids(IReadOnlyList<MessageRecord> fused) => [.. fused.Select(m => m.PublicId)];

    [Fact]
    public void K60_KnownRanking()
    {
        // With k = 60, one first place (1/61) loses to two 61st places (2/121) and beats two 63rd places (2/123).
        var first = Message("first");
        var twice61 = Message("twice61");
        var twice63 = Message("twice63");

        var fused = ReciprocalRankFusion.Fuse(
        [
            [Hit(first, 1), Hit(twice61, 61), Hit(twice63, 63)],
            [Hit(twice61, 61), Hit(twice63, 63)],
        ]);

        Ids(fused).ShouldBe(["twice61", "first", "twice63"]);
    }

    [Fact]
    public void DuplicateAcrossLists_Summed()
    {
        var both = Message("both");
        var vectorOnly = Message("vector-only", minute: 5);
        var lexicalOnly = Message("lexical-only", minute: 5);

        var fused = ReciprocalRankFusion.Fuse(
        [
            [Hit(vectorOnly, 1), Hit(both, 2)],
            [Hit(lexicalOnly, 1), Hit(both, 2)],
        ]);

        Ids(fused).ShouldBe(["both", "lexical-only", "vector-only"]);
    }

    [Fact]
    public void TieBreakIsDeterministic()
    {
        // Every message is first in one list, so all scores tie: newest first, then the higher Seq, then public id.
        var older = Message("a-older", minute: 0);
        var newer = Message("z-newer", minute: 1);
        var sameTimeLowSeq = Message("b-low-seq", minute: 2, seq: 1);
        var sameTimeHighSeq = Message("y-high-seq", minute: 2, seq: 2);
        var idB = Message("id-b", minute: 3, seq: 1) with { SessionName = "S2" };
        var idA = Message("id-a", minute: 3, seq: 1);
        IReadOnlyList<MessageRecord> all = [older, newer, sameTimeLowSeq, sameTimeHighSeq, idB, idA];

        string[] expected = ["id-a", "id-b", "y-high-seq", "b-low-seq", "z-newer", "a-older"];
        Ids(ReciprocalRankFusion.Fuse([.. all.Select(m => (IReadOnlyList<MessageHit>)[Hit(m, 1)])])).ShouldBe(expected);
        Ids(ReciprocalRankFusion.Fuse([.. all.Reverse().Select(m => (IReadOnlyList<MessageHit>)[Hit(m, 1)])])).ShouldBe(expected);
    }

    [Fact]
    public void NoLists_Empty()
    {
        ReciprocalRankFusion.Fuse([]).ShouldBeEmpty();
        ReciprocalRankFusion.Fuse([[], []]).ShouldBeEmpty();
    }
}
