using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;

namespace Nachos.DataLayer.SqlServer.Stores;

/// <summary>
/// The M3 stores the SQL provider does not implement yet: every member throws <see cref="NotSupportedException"/>.
/// M3 Task 3 replaces the summary and search members, and Tasks 7 and 8 the deletion members; then this class goes.
/// </summary>
internal sealed class UnimplementedRecallStores : ISummaryStore, IMessageSearch, IDeletionStore
{
    public const string Message = "Not implemented by the SQL Server provider yet (M3 Tasks 3, 7 and 8).";

    public Task<SummaryRecord?> GetAsync(string workspaceName, string sessionName, SummaryKind kind, CancellationToken ct) =>
        throw new NotSupportedException(Message);

    public Task<IReadOnlyList<SummaryRecord>> ListAsync(string workspaceName, string sessionName, CancellationToken ct) =>
        throw new NotSupportedException(Message);

    public Task SaveAsync(
        WorkLease lease, string workspaceName, string sessionName, IReadOnlyList<SummaryRecord> summaries, CancellationToken ct) =>
        throw new NotSupportedException(Message);

    public Task<IReadOnlyList<MessageHit>> VectorAsync(
        MessageSearchRequest request, ReadOnlyMemory<float> query, int take, CancellationToken ct) =>
        throw new NotSupportedException(Message);

    public Task<IReadOnlyList<MessageHit>> LexicalAsync(
        MessageSearchRequest request, IReadOnlyList<string> terms, int take, CancellationToken ct) =>
        throw new NotSupportedException(Message);

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
