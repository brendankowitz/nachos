using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Nachos.DataLayer.SqlServer.Schema;

namespace Nachos.DataLayer.SqlServer;

/// <summary>
/// The one rule every host uses to find its SQL Server connection in configuration: <see cref="CanonicalKey"/> (what
/// the infrastructure templates set) wins over <see cref="AspireKey"/> (what Aspire's <c>WithReference</c> sets), and a
/// key that exists must hold a usable value rather than fall back to the other one.
/// </summary>
public static class SqlServerConnectionSelection
{
    /// <summary>The setting the infrastructure templates (Bicep) provide.</summary>
    public const string CanonicalKey = SqlServerOptions.SectionName + ":ConnectionString";

    /// <summary>The setting Aspire's <c>WithReference</c> provides for the database named <c>nachos</c>.</summary>
    public const string AspireKey = "ConnectionStrings:nachos";

    /// <summary>True when either key exists, so the host is meant to use SQL Server.</summary>
    public static bool IsConfigured(IConfiguration configuration) => SelectedKey(configuration) is not null;

    /// <summary>The selected connection string.</summary>
    /// <exception cref="InvalidOperationException">
    /// Neither key exists, or the selected value is blank, malformed or names no database. The message names the key and
    /// never the value, which may carry credentials.
    /// </exception>
    public static string Select(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var key = SelectedKey(configuration)
            ?? throw new InvalidOperationException($"No SQL Server connection is configured. Set {CanonicalKey} or {AspireKey}.");
        var connectionString = configuration[key];
        return NamesDatabase(connectionString)
            ? connectionString!
            : throw new InvalidOperationException(
                $"Configuration value '{key}' must be a SQL Server connection string that names a database.");
    }

    private static string? SelectedKey(IConfiguration configuration) =>
        configuration.GetSection(CanonicalKey).Exists() ? CanonicalKey
        : configuration.GetSection(AspireKey).Exists() ? AspireKey
        : null;

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
