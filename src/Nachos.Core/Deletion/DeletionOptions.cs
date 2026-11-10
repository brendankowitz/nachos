namespace Nachos.Core.Deletion;

/// <summary>Deletion-job settings, bound from <c>Deletion</c>.</summary>
public sealed class DeletionOptions
{
    /// <summary>The most rows one purge transaction deletes, from 1 to <see cref="MaxBatchSize"/>; default 500.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Kept below SQL Server's lock escalation threshold (about 5,000 locks per statement), so a batch never locks a table.</summary>
    public const int MaxBatchSize = 4000;
}
