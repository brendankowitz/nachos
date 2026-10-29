namespace Nachos.Abstractions.Domain;

/// <summary>
/// Exclusive ownership of one work unit, proved by <paramref name="FencingToken"/>. Every write made under the lease
/// presents it, and is rejected with <see cref="LeaseLostException"/> once the lease is no longer held.
/// </summary>
/// <param name="FencingToken">Strictly greater than the token of every earlier lease on the same unit.</param>
/// <param name="Items">
/// The claimed items in ascending <see cref="WorkItemRecord.Id"/> order; empty for a lease taken with
/// <see cref="Stores.IWorkQueue.TryLeaseAsync"/>.
/// </param>
public sealed record WorkLease(string WorkUnitKey, string Owner, long FencingToken, IReadOnlyList<WorkItemRecord> Items)
{
    /// <summary>The longest owner identity a store accepts (the SQL <c>Owner nvarchar(128)</c> column).</summary>
    public const int MaxOwnerLength = 128;
}
