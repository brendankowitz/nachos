using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Shouldly;

namespace Nachos.Core.Tests.Memory;

public sealed class QueueStatusTests
{
    private const string W = MemoryFixture.W;

    private static readonly Dictionary<WorkTaskType, WorkBatchPolicy> Immediate =
        new() { [WorkTaskType.Representation] = WorkBatchPolicy.Immediate };

    [Fact]
    public async Task CountsWorkItems_OnePerMessage_PerSession_DeadLettersAsCompleted()
    {
        using var f = await new MemoryFixture().SeedAsync();
        await f.SayAsync("alice", "one", "S1");
        await f.SayAsync("alice", "two", "S1");
        await f.SayAsync("alice", "three", "S2");
        await f.SayAsync("alice", "four", "S2");
        (await f.Service.GetQueueStatusAsync(W) with { Sessions = null }).ShouldBe(new QueueStatus(4, 0, 0, 4, null));

        var lease = (await f.Store.WorkQueue.TryAcquireAsync("w1", Immediate, TimeSpan.FromMinutes(5), default)).ShouldNotBeNull();
        await f.Store.WorkQueue.CompleteAsync(lease, default);
        lease = (await f.Store.WorkQueue.TryAcquireAsync("w1", Immediate, TimeSpan.FromMinutes(5), default)).ShouldNotBeNull();
        await f.Store.WorkQueue.FailAsync(lease, "boom", TimeSpan.Zero, maxAttempts: 1, default);
        // S2's unit is leased for its first item; every open item of a leased unit counts as in progress.
        (await f.Store.WorkQueue.TryAcquireAsync("w1", Immediate, TimeSpan.FromMinutes(5), default)).ShouldNotBeNull();

        var status = await f.Service.GetQueueStatusAsync(W);

        // S1: one completed and one dead-lettered, both reported completed. S2: both in progress.
        status.ShouldSatisfyAllConditions(
            s => s.TotalWorkUnits.ShouldBe(4),
            s => s.CompletedWorkUnits.ShouldBe(2),
            s => s.InProgressWorkUnits.ShouldBe(2),
            s => s.PendingWorkUnits.ShouldBe(0));
        status.Sessions.ShouldNotBeNull();
        status.Sessions.Keys.Order(StringComparer.Ordinal).ShouldBe(["S1", "S2"]);
        status.Sessions["S1"].ShouldBe(new SessionQueueStatus("S1", 2, 2, 0, 0));
        status.Sessions["S2"].ShouldBe(new SessionQueueStatus("S2", 2, 0, 2, 0));
    }

    [Fact]
    public async Task Filters_ArePassedThrough()
    {
        using var f = await new MemoryFixture().SeedAsync();
        await f.SayAsync("alice", "one", "S1");
        await f.SayAsync("alice", "two", "S2");

        (await f.Service.GetQueueStatusAsync(W, sessionId: "S2")).ShouldSatisfyAllConditions(
            s => s.TotalWorkUnits.ShouldBe(1),
            s => s.Sessions!.Keys.ShouldBe(["S2"]));
        (await f.Service.GetQueueStatusAsync(W, senderId: "bob")).TotalWorkUnits.ShouldBe(0);
        (await f.Service.GetQueueStatusAsync(W, observerId: "bob")).TotalWorkUnits.ShouldBe(2);
        (await f.Service.GetQueueStatusAsync(W, observerId: "nobody")).Sessions.ShouldBe(new Dictionary<string, SessionQueueStatus>());
    }

    [Fact]
    public async Task InvalidIds_And_MissingWorkspace_AreRejected()
    {
        using var f = await new MemoryFixture().SeedAsync();

        await Should.ThrowAsync<NachosValidationException>(() => f.Service.GetQueueStatusAsync(W, observerId: "bad id"));
        await Should.ThrowAsync<NachosValidationException>(() => f.Service.GetQueueStatusAsync(W, senderId: "bad id"));
        await Should.ThrowAsync<NachosValidationException>(() => f.Service.GetQueueStatusAsync(W, sessionId: "bad id"));
        await Should.ThrowAsync<NotFoundException>(() => f.Service.GetQueueStatusAsync("missing"));
    }
}
