using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;

namespace Nachos.DataLayer.SqlServer.Stores;

/// <summary>
/// The deletion store the SQL provider does not implement yet: every member throws <see cref="NotSupportedException"/>.
/// M3 Tasks 7 and 8 replace it; then this class goes.
/// </summary>
internal sealed class UnimplementedDeletionStore : IDeletionStore
{
    public const string Message = "Not implemented by the SQL Server provider yet (M3 Tasks 7 and 8).";

    public Task<DeletionAcceptance> BeginDeleteSessionAsync(string workspaceName, string sessionName, CancellationToken ct) =>
        throw new NotSupportedException(Message);

    public Task<DeletionAcceptance> BeginDeleteWorkspaceAsync(string workspaceName, CancellationToken ct) =>
        throw new NotSupportedException(Message);

    public Task CancelOtherWorkAsync(WorkLease lease, DeletionTarget target, CancellationToken ct) =>
        throw new NotSupportedException(Message);

    public Task<bool> PurgeBatchAsync(WorkLease lease, DeletionTarget target, int batchSize, CancellationToken ct) =>
        throw new NotSupportedException(Message);

    public Task RemoveAsync(WorkLease lease, DeletionTarget target, CancellationToken ct) =>
        throw new NotSupportedException(Message);

    public Task<Page<JobRecord>> ListJobsAsync(string workspaceName, PageRequest page, CancellationToken ct) =>
        throw new NotSupportedException(Message);
}
