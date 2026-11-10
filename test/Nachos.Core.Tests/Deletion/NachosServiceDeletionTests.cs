using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Shouldly;

namespace Nachos.Core.Tests.Deletion;

/// <summary>The service entry points of queued deletion and job listing (Task 9 moves them onto <see cref="INachosClient"/>).</summary>
public sealed class NachosServiceDeletionTests : IDisposable
{
    private readonly InMemoryServiceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task DeleteSession_MarksInactiveAndListsOneJob()
    {
        await _fixture.Seed();

        await _fixture.Service.DeleteSessionAsync("W", "S");
        await _fixture.Service.DeleteSessionAsync("W", "S");

        (await _fixture.Service.ListSessionsAsync("W", null, new PageRequest())).Items.Single().IsActive.ShouldBeFalse();
        var job = (await _fixture.Service.ListJobsAsync("W", new PageRequest())).Items.ShouldHaveSingleItem();
        job.Target.ShouldBe(new JobTarget("session", "S"));
        job.Status.ShouldBe("pending");
        await Should.ThrowAsync<ConflictException>(() => _fixture.Service.UpdateSessionAsync("W", "S"));
    }

    [Fact]
    public async Task DeleteWorkspace_ActiveSessions_Conflicts()
    {
        await _fixture.Seed();

        var conflict = await Should.ThrowAsync<ConflictException>(() => _fixture.Service.DeleteWorkspaceAsync("W"));

        conflict.Message.ShouldBe("workspace has active sessions");
        (await _fixture.Service.ListJobsAsync("W", new PageRequest())).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteWorkspace_AfterItsSessions_ListsBothJobs()
    {
        await _fixture.Seed();
        await _fixture.Service.DeleteSessionAsync("W", "S");

        await _fixture.Service.DeleteWorkspaceAsync("W");

        (await _fixture.Service.ListJobsAsync("W", new PageRequest())).Items.Select(job => job.Target.Kind)
            .ShouldBe(["session", "workspace"]);
        await Should.ThrowAsync<ConflictException>(() => _fixture.Service.GetOrCreatePeerAsync("W", "alice"));
    }

    [Fact]
    public async Task Missing_NotFound()
    {
        await _fixture.Seed();

        await Should.ThrowAsync<NotFoundException>(() => _fixture.Service.DeleteSessionAsync("W", "missing"));
        await Should.ThrowAsync<NotFoundException>(() => _fixture.Service.DeleteWorkspaceAsync("missing"));
        await Should.ThrowAsync<NotFoundException>(() => _fixture.Service.ListJobsAsync("missing", new PageRequest()));
    }

    /// <summary>The methods are async, so a validation failure is a faulted task, not a synchronous throw.</summary>
    [Fact]
    public async Task InvalidIds_FaultTheTask()
    {
        var deleteSession = _fixture.Service.DeleteSessionAsync("W", "not valid!");
        var deleteWorkspace = _fixture.Service.DeleteWorkspaceAsync("");
        var listJobs = _fixture.Service.ListJobsAsync("not valid!", new PageRequest());

        await Should.ThrowAsync<NachosValidationException>(deleteSession);
        await Should.ThrowAsync<NachosValidationException>(deleteWorkspace);
        await Should.ThrowAsync<NachosValidationException>(listJobs);
    }
}
