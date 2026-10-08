using Microsoft.EntityFrameworkCore;
using Nachos.DataLayer.SqlServer.Schema;

namespace Nachos.DataLayer.SqlServer.Storage;

/// <summary>What every sub-store of one <see cref="SqlMemoryStore"/> shares: the clock, the re-entry guard and the gated way in.</summary>
internal sealed class SqlStoreRuntime(SqlServerOptions options, SchemaGate gate, TimeProvider clock)
{
    private readonly DbContextOptions<NachosDbContext> _contextOptions =
        new DbContextOptionsBuilder<NachosDbContext>().UseSqlServer(options.ConnectionString).Options;

    public TimeProvider Clock { get; } = clock;

    public ReentryGuard Guard { get; } = new();

    /// <summary>
    /// The way into the database for one operation: waits for <see cref="SchemaGate"/> (a remembered success after the
    /// first call), then returns a new context the caller disposes. Call <see cref="ReentryGuard.ThrowIfReentered"/> first.
    /// </summary>
    /// <exception cref="InvalidOperationException">The schema gate refused; the message names <c>nachos schema upgrade</c>.</exception>
    public async Task<NachosDbContext> OpenAsync(CancellationToken ct)
    {
        await gate.EnsureAsync(ct);
        return new NachosDbContext(_contextOptions);
    }
}
