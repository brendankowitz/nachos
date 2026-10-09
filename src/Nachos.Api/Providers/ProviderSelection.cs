using Microsoft.Data.SqlClient;
using Nachos.DataLayer.SqlServer.Schema;
using Nachos.Hosting;

namespace Nachos.Api.Providers;

/// <summary>Chooses the storage provider from configuration: SQL Server when a connection is configured, otherwise in-memory.</summary>
internal static class ProviderSelection
{
    /// <summary>The setting the infrastructure templates (Bicep) provide.</summary>
    internal const string CanonicalKey = SqlServerOptions.SectionName + ":ConnectionString";

    /// <summary>The setting Aspire's <c>WithReference</c> provides for the database named <c>nachos</c>.</summary>
    internal const string AspireKey = "ConnectionStrings:nachos";

    /// <summary>
    /// Selects SQL Server when <see cref="CanonicalKey"/> or <see cref="AspireKey"/> exists (the canonical key wins) and the
    /// in-memory provider when neither does. A key that exists must hold a usable connection string: a blank,
    /// malformed or database-less value fails startup rather than falling back to another source or to memory.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The chosen value is unusable. The message names the key and never the value, which may carry credentials.
    /// </exception>
    internal static NachosBuilder Use(NachosBuilder nachos, IConfiguration configuration)
    {
        var key = configuration.GetSection(CanonicalKey).Exists() ? CanonicalKey
            : configuration.GetSection(AspireKey).Exists() ? AspireKey
            : null;
        if (key is null)
        {
            return nachos.UseInMemory();
        }

        var connectionString = configuration[key];
        if (!NamesDatabase(connectionString))
        {
            throw new InvalidOperationException(
                $"Configuration value '{key}' must be a SQL Server connection string that names a database.");
        }

        return nachos.UseSqlServer(options =>
        {
            // The whole section binds so that existing options (AutomaticSchemaDeploymentEnabled) keep working; the
            // connection string chosen above then overrides whatever the section itself held.
            configuration.GetSection(SqlServerOptions.SectionName).Bind(options);
            options.ConnectionString = connectionString!;
        });
    }

    private static bool NamesDatabase(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        try
        {
            return !string.IsNullOrWhiteSpace(new SqlConnectionStringBuilder(connectionString).InitialCatalog);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }
    }
}
