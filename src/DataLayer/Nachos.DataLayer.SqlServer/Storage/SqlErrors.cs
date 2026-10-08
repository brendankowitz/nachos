using Microsoft.Data.SqlClient;

namespace Nachos.DataLayer.SqlServer.Storage;

/// <summary>SQL Server error numbers this provider reacts to.</summary>
internal static class SqlErrors
{
    /// <summary>Violation of a PRIMARY KEY or UNIQUE constraint (2627) or of a unique index (2601).</summary>
    public static bool IsUniqueViolation(Exception exception) =>
        Find(exception) is { } sql && sql.Errors.Cast<SqlError>().Any(error => error.Number is 2627 or 2601);

    // EF Core may wrap the provider exception; the SqlException is then the inner one.
    private static SqlException? Find(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is SqlException sql)
            {
                return sql;
            }
        }

        return null;
    }
}
