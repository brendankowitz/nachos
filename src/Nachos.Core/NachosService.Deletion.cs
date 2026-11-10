using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;

namespace Nachos.Core;

/// <summary>Queued deletion (<c>DELETE W</c>, <c>DELETE S</c>, both 202) and job listing (<c>GET W/jobs</c>, D6).</summary>
/// <remarks>
/// These are not yet on <see cref="INachosClient"/>: the interface is one method per wire route, implemented over HTTP
/// too, and the routes and the HTTP client arrive together in M3 Task 9. A delete only accepts the job; the job removes the
/// resource later, and its progress is listed by <see cref="ListJobsAsync"/>.
/// </remarks>
public sealed partial class NachosService
{
    /// <summary>
    /// Accepts the session's deletion: it is <c>is_active = false</c> from now on, every write under it is a 409, and a job
    /// removes it with its messages, summaries and session-attributed conclusions. Repeating it is harmless.
    /// </summary>
    /// <exception cref="NotFoundException">The workspace or the session does not exist.</exception>
    /// <exception cref="ConflictException">The workspace is being deleted.</exception>
    public async Task DeleteSessionAsync(string workspaceId, string sessionId, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (sessionId, "session_id"));
        await store.Deletion.BeginDeleteSessionAsync(workspaceId, sessionId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts the workspace's deletion: every write under it is a 409 from now on, and a job removes it with everything in
    /// it. Repeating it is harmless.
    /// </summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    /// <exception cref="ConflictException">The workspace has an active session.</exception>
    public async Task DeleteWorkspaceAsync(string workspaceId, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"));
        await store.Deletion.BeginDeleteWorkspaceAsync(workspaceId, ct).ConfigureAwait(false);
    }

    /// <summary>The workspace's jobs (in M3, its deletions and its sessions').</summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    public async Task<Page<Job>> ListJobsAsync(string workspaceId, PageRequest page, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"));
        ArgumentNullException.ThrowIfNull(page);
        return await jobs.ListAsync(workspaceId, page, ct).ConfigureAwait(false);
    }
}
