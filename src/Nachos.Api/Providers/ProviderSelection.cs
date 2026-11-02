using Nachos.DataLayer.SqlServer;
using Nachos.Hosting;

namespace Nachos.Api.Providers;

/// <summary>Chooses the storage provider from configuration: SQL Server when a connection is configured, otherwise in-memory.</summary>
internal static class ProviderSelection
{
    /// <summary>
    /// Selects SQL Server when <see cref="SqlServerConnectionSelection.CanonicalKey"/> or
    /// <see cref="SqlServerConnectionSelection.AspireKey"/> exists (the canonical key wins) and the in-memory provider when
    /// neither does. A key that exists must hold a usable connection string: a blank, malformed or database-less value
    /// fails startup rather than falling back to another source or to memory. The worker host uses the same rule.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The chosen value is unusable. The message names the key and never the value, which may carry credentials.
    /// </exception>
    internal static NachosBuilder Use(NachosBuilder nachos, IConfiguration configuration) =>
        UsesSqlServer(configuration) ? nachos.UseSqlServer(configuration) : nachos.UseInMemory();

    /// <summary>True when <see cref="Use"/> will select SQL Server, so a separate worker host owns the background loops.</summary>
    internal static bool UsesSqlServer(IConfiguration configuration) => SqlServerConnectionSelection.IsConfigured(configuration);
}
