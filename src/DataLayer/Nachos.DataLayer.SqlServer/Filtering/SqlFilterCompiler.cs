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
/// only generated parameter names, generated aliases and fixed fragments. A list (<c>in</c> on a column, containment)
/// travels as one JSON parameter read with <c>OPENJSON</c>; a metadata <c>in</c> list (or OR-ed metadata equalities on
/// one path) travels packed into <c>varchar(8000)</c> chunks, described below. Equal values share a
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
/// <c>CHARINDEX</c> under the same collation, after a length check (<c>LIKE '%…%'</c> is quadratic on repetitive text);
/// an operand too long for <c>CHARINDEX</c> to search for (4000 code units) is found by its prefix and confirmed by
/// comparing it with every substring of its length. <c>icontains</c> applies <c>UPPER</c> to both sides under the same
/// binary collation.
/// </para>
/// <para>
/// <b>Metadata</b> is walked key by key with <c>OPENJSON</c> (each step must be an object), and values compare only
/// within one JSON kind using <c>OPENJSON</c>'s <c>type</c> column. <b>Numbers</b> compare exactly, with no
/// approximation and no size or precision limit: metadata stores number text exactly as written (exponent forms
/// included), and the schema function <c>dbo.JsonNumberOrderKey</c> maps that text to a variable-length key (sign, the
/// exponent of the first significant digit as an arbitrary-size integer, then the significant digits) that sorts like the
/// value; the operand's key is computed in C# by the same algorithm (<c>Storage.ExactDecimal.ToOrderKey</c>). So
/// <c>1e2</c> equals <c>100</c>, and <c>1E400</c>, <c>5E-324</c> or 2^96 + 1 compare exactly.
/// </para>
/// <para>
/// <b>Many conditions.</b> A filter with one metadata condition (after merging) is an <c>EXISTS</c> over its path. With
/// more, they become flags over a single read of the row's metadata: each entry's key is looked up once among the keys
/// the filter names, entries under other keys are skipped, one aggregate per row computes each flag from the entries under
/// its keys, and the filter's AND/OR/NOT is evaluated over the flags. Conditions OR-ed together (and unset/<c>ne</c>
/// conditions AND-ed together) share flags of up to 32 conditions, a condition repeated under many keys is tested once for
/// all of them, and identical conditions share one flag. The parser sets no cap on the number of conditions, but SQL
/// Server bounds what it can compile: a filtered list whose statement fails with an expression-services or optimizer
/// resource limit (errors 8632, 8623, 8621, 191) is a <see cref="NachosValidationException"/> with the fixed detail
/// <see cref="TooComplex"/>, like a filter beyond the parameter limit. Measured: tens of thousands of conditions that
/// share their tests run (10,000 in seconds), while about 3,500 conditions that share nothing (an OR of ANDs on distinct
/// keys) are refused. The largest filters cost mostly compile time.
/// </para>
/// <para>
/// <b>Metadata <c>in</c> lists</b> are packed so that a row's cost does not grow with the list: entries are sorted into
/// <c>varchar(8000)</c> chunks passed with their first and last entry, and a row searches only the chunk whose range
/// holds its own entry. Short operands compare exactly: strings of up to 16 UTF-16 code units as the hex of their code
/// units, number order keys of up to 100 characters as themselves. Long-operand <c>in</c> membership uses a SHA-256 + kind
/// + length prefilter, confirmed by exact comparison: a longer string's entry is its UTF-16 code-unit count and the
/// SHA-256 of its UTF-16LE code units (<c>len:HEX</c>), a longer key's its length and the SHA-256 of the key, each
/// tested only against stored values of the same JSON kind. An entry match only locates the one operand it names, whose
/// bytes are then compared with the stored value's bytes, so a digest collision can never produce a false match.
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
    public static (string Sql, IReadOnlyList<SqlParameter> Parameters) Compile(FilterNode? filter, ResourceKind kind, string tableAlias) =>
        Compile(filter, kind, tableAlias, SqlDigest.FullHexLength);

    /// <summary>
    /// Test seam: <see cref="Compile(FilterNode?, ResourceKind, string)"/> with prefilter digests shortened to
    /// <paramref name="digestHexLength"/> hex digits (0 to 64), so tests can force digest collisions between different
    /// operands of one length and prove that the exact confirmation decides membership. Production always uses 64.
    /// </summary>
    internal static (string Sql, IReadOnlyList<SqlParameter> Parameters) Compile(FilterNode? filter, ResourceKind kind, string tableAlias, int digestHexLength)
    {
        if (!SafeAlias().IsMatch(tableAlias))
        {
            throw new ArgumentException("The table alias must be a plain identifier.", nameof(tableAlias));
        }

        if (filter is null)
        {
            return (FilterWriter.True, []);
        }

        var writer = new FilterWriter(kind, tableAlias, digestHexLength);
        var sql = writer.WriteFilter(filter);
        return writer.Parameters.Count <= MaxParameters
            ? (sql, writer.Parameters)
            : throw new NachosValidationException(TooManyValues);
    }

    /// <summary>The fixed detail of the 422 for a filter beyond SQL Server's parameter limit.</summary>
    public const string TooManyValues = "The filter needs more distinct values than the SQL Server provider can send in one statement.";

    /// <summary>
    /// The fixed detail of the 422 for a filter whose statement SQL Server cannot compile (<see cref="Storage.SqlErrors.IsTooComplex"/>);
    /// the stores translate those errors when they run a filtered list.
    /// </summary>
    public const string TooComplex = "The filter is too complex for the SQL Server provider to run in one statement.";

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeAlias();
}
