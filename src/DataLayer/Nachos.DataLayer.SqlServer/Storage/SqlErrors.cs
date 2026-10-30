using Microsoft.Data.SqlClient;

namespace Nachos.DataLayer.SqlServer.Storage;

/// <summary>SQL Server error numbers this provider reacts to.</summary>
internal static class SqlErrors
{
    /// <summary>Violation of a PRIMARY KEY or UNIQUE constraint (2627) or of a unique index (2601).</summary>
    public static bool IsUniqueViolation(Exception exception) =>
        Find(exception) is { } sql && sql.Errors.Cast<SqlError>().Any(error => error.Number is 2627 or 2601);

    /// <summary>The transaction was chosen as a deadlock victim (1205) and rolled back.</summary>
    public static bool IsDeadlockVictim(Exception exception) =>
        Find(exception) is { } sql && sql.Errors.Cast<SqlError>().Any(error => error.Number == 1205);

    /// <summary>
    /// The statement is beyond what SQL Server can compile: an expression-services limit (8632), the query processor
    /// running out of internal resources (8623) or of stack space while optimizing (8621), or nesting too deep (191). For a
    /// filtered list, a property of the filter.
    /// </summary>
    public static bool IsTooComplex(Exception exception) =>
        Find(exception) is { } sql && sql.Errors.Cast<SqlError>().Any(error => error.Number is 8632 or 8623 or 8621 or 191);

    /// <summary>
    /// A failure that retrying later can fix: a deadlock (1205), a command timeout (-2), a dropped or refused connection
    /// (20, 64, 233, 10053, 10054, 10060), a database that cannot be opened yet or is failing over (4060, 40197, 40613),
    /// or Azure SQL throttling (10928, 10929, 40501, 49918, 49919, 49920).
    /// </summary>
    /// <remarks>
    /// A list, because <see cref="SqlException"/> does not override <see cref="System.Data.Common.DbException.IsTransient"/>
    /// (always false) in Microsoft.Data.SqlClient 6.1.
    /// </remarks>
    public static bool IsTransient(Exception exception) =>
        Find(exception) is { } sql && sql.Errors.Cast<SqlError>().Any(error => error.Number is
            1205 or -2 or 20 or 64 or 233 or 10053 or 10054 or 10060 or 4060 or 40197 or 40613
            or 10928 or 10929 or 40501 or 49918 or 49919 or 49920);

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
