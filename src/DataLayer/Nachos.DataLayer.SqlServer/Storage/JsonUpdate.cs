using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Nachos.DataLayer.SqlServer.Storage;

/// <summary>The "replace metadata and/or configuration" update shared by workspaces, peers and sessions.</summary>
internal static class JsonUpdate
{
    /// <summary>
    /// Replaces the given JSON columns of the row matching <paramref name="where"/> in one statement and returns the
    /// updated row; null when both values are null (nothing to change) or no row matched.
    /// </summary>
    /// <param name="table">A fixed table name chosen by the caller (never user input).</param>
    /// <param name="where">A fixed predicate over the caller's <paramref name="keys"/> (never user input).</param>
    public static async Task<TEntity?> ApplyAsync<TEntity>(
        DbSet<TEntity> rows,
        string table,
        string where,
        string? metadata,
        string? configuration,
        IReadOnlyList<SqlParameter> keys,
        CancellationToken ct)
        where TEntity : class
    {
        if (metadata is null && configuration is null)
        {
            return null;
        }

        var assignments = new List<string>(2);
        var parameters = new List<SqlParameter>(keys);
        if (metadata is not null)
        {
            assignments.Add("Metadata = @metadata");
            parameters.Add(SqlParameters.LongText("@metadata", metadata));
        }

        if (configuration is not null)
        {
            assignments.Add("Configuration = @configuration");
            parameters.Add(SqlParameters.LongText("@configuration", configuration));
        }

        var sql = string.Concat("UPDATE ", table, " SET ", string.Join(", ", assignments), " OUTPUT inserted.* WHERE ", where);
        var updated = await rows.FromSqlRaw(sql, [.. parameters]).AsNoTracking().ToListAsync(ct);
        return updated.SingleOrDefault();
    }
}
