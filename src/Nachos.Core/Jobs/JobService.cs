using System.Globalization;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Work;

namespace Nachos.Core.Jobs;

/// <summary><c>GET W/jobs</c> (a Nachos extension, D6): a workspace's background jobs in the wire shape. In M3 these are its deletions.</summary>
public sealed class JobService(IMemoryStore store)
{
    /// <summary>The workspace's jobs, oldest first (newest first when <see cref="PageRequest.Reverse"/>).</summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    public async Task<Page<Job>> ListAsync(string workspaceId, PageRequest page, CancellationToken ct)
    {
        var jobs = await store.Deletion.ListJobsAsync(workspaceId, page, ct).ConfigureAwait(false);
        return new Page<Job>([.. jobs.Items.Select(Map)], jobs.Total, jobs.PageNumber, jobs.Size, jobs.Pages);
    }

    private static Job Map(JobRecord job) => new(
        job.Id.ToString(CultureInfo.InvariantCulture),
        TypeName(job.Type),
        new JobTarget(DeletionPayload.Name(job.Target.Kind), job.Target.Name),
        StatusName(job.Status),
        job.Attempts,
        job.Error,
        job.CreatedAt,
        job.CompletedAt);

    private static string TypeName(WorkTaskType type) => type switch
    {
        WorkTaskType.Deletion => "deletion",
        _ => throw new InvalidOperationException($"Work task type {type} is not listed as a job."),
    };

    private static string StatusName(JobStatus status) => status switch
    {
        JobStatus.Pending => "pending",
        JobStatus.InProgress => "in_progress",
        JobStatus.Completed => "completed",
        JobStatus.DeadLettered => "dead_lettered",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown job status."),
    };
}
