using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Nachos.Abstractions;
using Nachos.Abstractions.Filtering;

namespace Nachos.DataLayer.SqlServer.Filtering;

/// <summary>
/// Compiles a parsed <see cref="FilterNode"/> into a T-SQL predicate over one table alias, implementing the semantics
/// documented on <see cref="FilterNode"/>, <see cref="FilterOp"/> and <see cref="FilterColumns"/> (pinned by the shared
/// <c>filter-cases.json</c> and by <c>SqlFilterDifferentialTests</c> against the in-memory provider).
/// </summary>
/// <remarks>
/// <para>
/// <b>Parameters only.</b> No operand, list element or metadata key is ever written into the SQL text: the text holds
/// only generated parameter names, generated aliases and fixed fragments. A list (<c>in</c>, containment, or OR-ed
/// metadata equalities on one path) travels as one JSON parameter read with <c>OPENJSON</c>. Equal values share a
/// parameter. A filter needing more than <see cref="MaxParameters"/> parameters is rejected with
/// <see cref="NachosValidationException"/> instead of failing at SQL Server's 2,100-parameter limit.
/// </para>
/// <para>
/// <b>Two-valued.</b> Every predicate is TRUE or FALSE, never UNKNOWN (rows are tested with <c>EXISTS</c> and non-null
/// columns), so <c>NOT</c> means exactly "does not match".
/// </para>
/// <para>
/// <b>Text</b> compares by UTF-16 code unit under <c>Latin1_General_100_BIN2</c> (a non-supplementary binary collation,
/// whose order is .NET's ordinal order). SQL pads with spaces before <c>=</c> and <c>&lt;</c>, so equality also compares
/// <c>DATALENGTH</c>, and ordering compares the common-length prefixes and then the lengths. <c>contains</c> is
/// <c>LIKE</c> with <c>%</c>, <c>_</c>, <c>[</c> and the escape character escaped; an operand too long for a
/// <c>LIKE</c> pattern (4000 characters) is matched by comparing it with every substring of its length.
/// <c>icontains</c> applies <c>UPPER</c> to both sides under the same binary collation.
/// </para>
/// <para>
/// <b>Metadata</b> is walked key by key with <c>OPENJSON</c> (each step must be an object), and values compare only
/// within one JSON kind using <c>OPENJSON</c>'s <c>type</c> column. <b>Numbers</b> compare exactly: stored numbers are
/// plain decimals of at most 38 digits (see <c>Storage.SqlJson</c>), which SQL maps to a 76-digit order key; the operand's
/// key is computed exactly in C# (see <c>Storage.ExactDecimal</c>), including operands beyond that window.
/// </para>
/// </remarks>
internal static partial class SqlFilterCompiler
{
    /// <summary>The most parameters one filter may use, leaving room for the store's own.</summary>
    public const int MaxParameters = 2000;

    /// <summary>Compiles <paramref name="filter"/>; null compiles to a predicate that matches every row.</summary>
    /// <param name="tableAlias">The alias of the resource's table in the caller's query: an identifier, never user input.</param>
    /// <exception cref="ArgumentException"><paramref name="tableAlias"/> is not a plain identifier.</exception>
    /// <exception cref="NachosValidationException">The filter needs more than <see cref="MaxParameters"/> parameters.</exception>
    public static (string Sql, IReadOnlyList<SqlParameter> Parameters) Compile(FilterNode? filter, ResourceKind kind, string tableAlias)
    {
        if (!SafeAlias().IsMatch(tableAlias))
        {
            throw new ArgumentException("The table alias must be a plain identifier.", nameof(tableAlias));
        }

        if (filter is null)
        {
            return (FilterWriter.True, []);
        }

        var writer = new FilterWriter(kind, tableAlias);
        var sql = writer.Write(filter);
        return writer.Parameters.Count <= MaxParameters
            ? (sql, writer.Parameters)
            : throw new NachosValidationException(
                $"The filter has too many distinct values ({writer.Parameters.Count}); the limit is {MaxParameters}. Use 'in' lists to combine values.");
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeAlias();
}
