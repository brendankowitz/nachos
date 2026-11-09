using System.Text.Json.Nodes;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Work;
using Shouldly;
using Xunit;

namespace Nachos.Abstractions.Tests;

/// <summary>The M3 work keys and payloads, and when a summary comes due (plan D3).</summary>
public sealed class M3WorkTests
{
    [Fact]
    public void SummaryAndDeletionKeys_FollowTheRepresentationRules()
    {
        WorkUnitKeys.Summary("ws_1", "s-1").ShouldBe("summary:ws_1:s-1");
        WorkUnitKeys.Deletion("ws", DeletionTargetKind.Session, "s").ShouldBe("deletion:ws:session:s");
        DeletionTarget.Workspace("ws").WorkUnitKey.ShouldBe("deletion:ws:workspace:ws");

        var name = new string('a', 512);
        WorkUnitKeys.Summary(name, name).Length.ShouldBeLessThanOrEqualTo(WorkUnitKeys.MaxLength);
        WorkUnitKeys.Deletion(name, DeletionTargetKind.Session, name).ShouldBe(DeletionTarget.Session(name, name).WorkUnitKey);
        WorkUnitKeys.Summary("a:b", "c").ShouldNotBe(WorkUnitKeys.Summary("a", "b:c"));
    }

    [Theory]
    [InlineData(18, 23, 20, 60, 20L, null)]
    [InlineData(59, 61, 20, 60, 60L, 60L)]
    [InlineData(1, 45, 20, 60, 40L, null)]
    [InlineData(1, 19, 20, 60, null, null)]
    [InlineData(21, 39, 20, 60, null, null)]
    [InlineData(5, 5, 1, 5, 5L, 5L)]
    public void DueThrough_IsTheHighestMultipleInTheBatch(long first, long last, int shortEvery, int longEvery, long? expectedShort, long? expectedLong)
    {
        var intent = new SummaryIntent(true, shortEvery, longEvery);

        intent.DueThrough(SummaryKind.Short, first, last).ShouldBe(expectedShort);
        intent.DueThrough(SummaryKind.Long, first, last).ShouldBe(expectedLong);
        (intent with { Enabled = false }).DueThrough(SummaryKind.Short, first, last).ShouldBeNull();
    }

    [Fact]
    public void SummaryIntent_RejectsIntervalsBelowOne()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new SummaryIntent(true, 0, 60));
        Should.Throw<ArgumentOutOfRangeException>(() => new SummaryIntent(true, 20, 60) with { LongEvery = -1 });
    }

    [Fact]
    public void SummaryPayload_RoundTrips_AndRejectsMalformed()
    {
        var payload = SummaryPayload.Create(SummaryKind.Long, 60);

        payload.ToJsonString().ShouldBe("""{"kind":"long","through_seq":60}""");
        SummaryPayload.Read(payload).ShouldBe(new SummaryWork(SummaryKind.Long, 60));
        Should.Throw<FormatException>(() => SummaryPayload.Read(new JsonObject { ["kind"] = "medium", ["through_seq"] = 1 }));
        Should.Throw<FormatException>(() => SummaryPayload.Read(new JsonObject { ["kind"] = "short", ["through_seq"] = 1.5 }));
        Should.Throw<FormatException>(() => SummaryPayload.Read(new JsonObject { ["kind"] = "short" }));
    }

    [Fact]
    public void DeletionPayload_ReadsTheTargetFromItsItem()
    {
        WorkItemRecord Item(JsonObject payload, string? session, WorkTaskType type = WorkTaskType.Deletion) =>
            new(1, "ws", type, "key", payload, 1, 0, session, null, DateTimeOffset.UnixEpoch);

        DeletionPayload.Read(Item(DeletionPayload.Create(DeletionTargetKind.Session), "s")).ShouldBe(DeletionTarget.Session("ws", "s"));
        DeletionPayload.Read(Item(DeletionPayload.Create(DeletionTargetKind.Workspace), null)).ShouldBe(DeletionTarget.Workspace("ws"));
        Should.Throw<FormatException>(() => DeletionPayload.Read(Item(DeletionPayload.Create(DeletionTargetKind.Workspace), "s")));
        Should.Throw<FormatException>(() => DeletionPayload.Read(Item(DeletionPayload.Create(DeletionTargetKind.Session), "s", WorkTaskType.Summary)));
    }
}
