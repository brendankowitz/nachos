using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Core.Configuration;
using Nachos.Core.Deletion;
using Nachos.Core.Jobs;
using Nachos.DataLayer.InMemory;
using Shouldly;

namespace Nachos.Core.Tests.Deletion;

/// <summary><c>GET W/jobs</c> (D6): the workspace's deletion jobs in the wire shape.</summary>
public sealed class JobServiceTests
{
    private const string Workspace = "W";

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero));

    private readonly InMemoryMemoryStore _store;

    private readonly JobService _jobs;

    public JobServiceTests()
    {
        _store = new InMemoryMemoryStore(_clock);
        _jobs = new JobService(_store);
    }

    [Fact]
    public async Task ListsDeletionJobsWithStatus()
    {
        await SeedAsync("completed", "dead", "running", "waiting");
        var completed = await _store.Deletion.BeginDeleteSessionAsync(Workspace, "completed", Ct);
        await Handler().HandleAsync(await AcquireAsync(), Ct);
        var completedAt = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromSeconds(1));
        var dead = await _store.Deletion.BeginDeleteSessionAsync(Workspace, "dead", Ct);
        await _store.WorkQueue.FailAsync(await AcquireAsync(), "boom", TimeSpan.Zero, maxAttempts: 1, Ct);
        var running = await _store.Deletion.BeginDeleteSessionAsync(Workspace, "running", Ct);
        await AcquireAsync();
        var waiting = await _store.Deletion.BeginDeleteSessionAsync(Workspace, "waiting", Ct);
        var workspace = await _store.Deletion.BeginDeleteWorkspaceAsync(Workspace, Ct);

        var page = await _jobs.ListAsync(Workspace, new PageRequest(), Ct);

        page.Total.ShouldBe(5);
        page.Items.Select(job => (job.Id, job.Type, job.Target, job.Status, job.Attempts, job.Error)).ShouldBe(
        [
            (Id(completed), "deletion", new JobTarget("session", "completed"), "completed", 1, null),
            (Id(dead), "deletion", new JobTarget("session", "dead"), "dead_lettered", 1, "boom"),
            (Id(running), "deletion", new JobTarget("session", "running"), "in_progress", 1, null),
            (Id(waiting), "deletion", new JobTarget("session", "waiting"), "pending", 0, null),
            (Id(workspace), "deletion", new JobTarget("workspace", Workspace), "pending", 0, null),
        ]);
        page.Items[0].CreatedAt.ShouldBe(completedAt);
        page.Items[0].CompletedAt.ShouldBe(completedAt);
        page.Items[1].CompletedAt.ShouldNotBeNull();
        page.Items.Skip(2).ShouldAllBe(job => job.CompletedAt == null);
    }

    [Fact]
    public async Task Paged()
    {
        await SeedAsync("a", "b", "c");
        var ids = new List<string>();
        foreach (var session in new[] { "a", "b", "c" })
        {
            ids.Add(Id(await _store.Deletion.BeginDeleteSessionAsync(Workspace, session, Ct)));
        }

        var first = await _jobs.ListAsync(Workspace, new PageRequest(Page: 1, Size: 2), Ct);
        var second = await _jobs.ListAsync(Workspace, new PageRequest(Page: 2, Size: 2), Ct);
        var newest = await _jobs.ListAsync(Workspace, new PageRequest(Page: 1, Size: 2, Reverse: true), Ct);

        (first.Total, first.PageNumber, first.Size, first.Pages).ShouldBe((3L, 1, 2, 2));
        first.Items.Select(job => job.Id).ShouldBe(ids[..2]);
        second.Items.Select(job => job.Id).ShouldBe([ids[2]]);
        newest.Items.Select(job => job.Id).ShouldBe([ids[2], ids[1]]);
    }

    [Fact]
    public async Task MissingWorkspace_NotFound()
    {
        await Should.ThrowAsync<NotFoundException>(() => _jobs.ListAsync("missing", new PageRequest(), Ct));
    }

    private static string Id(DeletionAcceptance acceptance) => acceptance.JobId.ToString(CultureInfo.InvariantCulture);

    private async Task SeedAsync(params string[] sessions)
    {
        await _store.Workspaces.GetOrCreateAsync(Workspace, null, null, Ct);
        foreach (var session in sessions)
        {
            await _store.Sessions.GetOrCreateAsync(Workspace, session, null, null, null, Ct);
        }
    }

    private async Task<WorkLease> AcquireAsync() =>
        (await _store.WorkQueue.TryAcquireAsync(
            "worker", new Dictionary<WorkTaskType, WorkBatchPolicy> { [WorkTaskType.Deletion] = WorkBatchPolicy.Immediate }, Lease, Ct))
        .ShouldNotBeNull();

    private DeletionService Handler() =>
        new(_store, Options.Create(new NachosOptions()), Options.Create(new DeletionOptions()), NullLogger<DeletionService>.Instance);
}
