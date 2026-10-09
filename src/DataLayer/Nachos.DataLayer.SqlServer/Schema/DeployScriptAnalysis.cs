using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Nachos.Abstractions.Schema;

namespace Nachos.DataLayer.SqlServer.Schema;

/// <summary>
/// Reads the script DacFx generates for a deploy. The DeployReport names tables and constraints but not what is done to them,
/// and it says nothing at all about database options, so the script is the only place the real effect shows.
/// </summary>
/// <remarks>
/// Fails closed, in two ways.
/// <list type="bullet">
/// <item>Per table: a table is <see cref="DeployClassification.AutoSafe"/> only when the script touches it with
/// <c>ALTER TABLE … ADD</c> of plain columns that are nullable or have a <c>DEFAULT</c>, and nothing else.</item>
/// <item>Whole script: only statements DacFx emits for additive changes are allowed (see <see cref="IsAllowed"/>). Any other
/// statement, such as a <c>DELETE</c>, <c>DROP INDEX</c> or <c>DBCC</c>, is listed in <see cref="DisallowedStatements"/>, and any
/// <c>ALTER DATABASE</c> is reported in <see cref="DatabaseOptionChanges"/>.</item>
/// </list>
/// A script that does not parse, or that runs dynamic SQL or an unknown procedure, proves nothing: its tables are
/// <see cref="DeployClassification.Unclassifiable"/> and <see cref="HasOpaqueExecution"/> is set.
/// </remarks>
internal sealed partial class DeployScriptAnalysis
{
    // Procedures a deploy script may call without changing what it does to a table's columns.
    private static readonly HashSet<string> HarmlessProcedures = new(StringComparer.OrdinalIgnoreCase)
    {
        "sp_refreshsqlmodule", "sp_refreshview",
    };

    private readonly Dictionary<string, bool> _columnAdditionsAreSafe = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _otherwiseChanged = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _databaseOptionChanges = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _disallowedStatements = new(StringComparer.Ordinal);

    private DeployScriptAnalysis()
    {
    }

    /// <summary>True when the script runs something whose effect cannot be read from it (dynamic SQL, an unknown procedure).</summary>
    public bool HasOpaqueExecution { get; private set; }

    /// <summary>The database options the script would change (<c>READ_COMMITTED_SNAPSHOT</c>, <c>PAGE_VERIFY</c>, …), or a statement name for any other <c>ALTER DATABASE</c>.</summary>
    public IReadOnlyCollection<string> DatabaseOptionChanges => _databaseOptionChanges;

    /// <summary>The kinds of statement in the script that are not on the allowlist, other than <c>ALTER DATABASE</c>.</summary>
    public IReadOnlyCollection<string> DisallowedStatements => _disallowedStatements;

    /// <summary>Parses the script, or returns null when any part of it cannot be parsed.</summary>
    public static DeployScriptAnalysis? TryParse(string script)
    {
        var analysis = new DeployScriptAnalysis();
        var visitor = new Visitor(analysis);
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);

        foreach (var batch in SplitBatches(script))
        {
            using var reader = new StringReader(batch);
            var fragment = parser.Parse(reader, out var errors);
            if (errors.Count > 0)
            {
                return null;
            }

            fragment.Accept(visitor);
        }

        return analysis;
    }

    /// <summary>How the script changes <paramref name="table"/> (spelled <c>[dbo].[Name]</c>).</summary>
    public DeployClassification ClassifyTable(string table)
    {
        if (HasOpaqueExecution)
        {
            return DeployClassification.Unclassifiable;
        }

        if (_otherwiseChanged.Contains(table))
        {
            return DeployClassification.Unsafe;
        }

        return _columnAdditionsAreSafe.TryGetValue(table, out var safe)
            ? safe ? DeployClassification.AutoSafe : DeployClassification.Unsafe
            : DeployClassification.Unclassifiable;
    }

    // SQLCMD directives (:setvar, :on error) and GO are not T-SQL; DacFx scripts use both.
    private static IEnumerable<string> SplitBatches(string script)
    {
        var batch = new System.Text.StringBuilder();
        foreach (var line in script.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                if (batch.Length > 0)
                {
                    yield return batch.ToString();
                    batch.Clear();
                }
            }
            else if (!trimmed.StartsWith(':'))
            {
                batch.AppendLine(line.TrimEnd('\r'));
            }
        }

        if (batch.Length > 0)
        {
            yield return batch.ToString();
        }
    }

    private static string Spell(SchemaObjectName name) =>
        $"[{name.SchemaIdentifier?.Value ?? "dbo"}].[{name.BaseIdentifier.Value}]";

    private static bool IsSafeAddedColumn(ColumnDefinition column) =>
        column.ComputedColumnExpression is null
        && column.IdentityOptions is null
        && column.Index is null
        && column.Constraints.All(c => c is NullableConstraintDefinition or DefaultConstraintDefinition)
        && (column.DefaultConstraint is not null
            || column.Constraints.OfType<NullableConstraintDefinition>().Any(n => n.Nullable));

    /// <summary>
    /// The statements DacFx 170 emits for the changes the classifier allows: creating tables, indexes, constraints and routines,
    /// adding columns, and the scaffolding around them (<c>USE</c>, <c>SET</c>, <c>PRINT</c>, <c>IF</c>/<c>BEGIN … END</c>, the
    /// data-loss <c>RAISERROR</c>, transactions), plus the post-deployment stamp, a <c>MERGE</c> into <c>dbo.SchemaVersion</c>,
    /// and <c>GRANT EXECUTE</c> on a single object of this database (see <see cref="IsExecuteGrant"/>), which the schema uses to
    /// let any user call its pure functions. Constraints are only ever re-enabled (<c>WITH CHECK CHECK</c>), never disabled. <c>EXECUTE</c> is allowed here and
    /// judged in <see cref="Visitor"/>. <c>ALTER DATABASE</c> is not allowed; it is reported separately.
    /// </summary>
    private static bool IsAllowed(TSqlStatement statement) => statement switch
    {
        PrintStatement or PredicateSetStatement or IfStatement or BeginEndBlockStatement or RaiseErrorStatement => true,
        UseStatement use => use.DatabaseName.Value == DeployedDatabaseVariable,
        BeginTransactionStatement or CommitTransactionStatement or RollbackTransactionStatement or SetTransactionIsolationLevelStatement => true,
        CreateTableStatement or CreateIndexStatement => true,
        CreateProcedureStatement or AlterProcedureStatement or CreateFunctionStatement or AlterFunctionStatement => true,
        CreateViewStatement or AlterViewStatement => true,
        AlterTableAddTableElementStatement => true,
        AlterTableConstraintModificationStatement reenable =>
            reenable.ExistingRowsCheckEnforcement == ConstraintEnforcement.Check && reenable.ConstraintEnforcement == ConstraintEnforcement.Check,
        MergeStatement merge => IsSchemaVersionStamp(merge),
        GrantStatement grant => IsExecuteGrant(grant),
        ExecuteStatement => true,
        _ => false,
    };

    // DacFx scripts always address the target as the SQLCMD variable; any other USE is somewhere it did not choose.
    private const string DeployedDatabaseVariable = "$(DatabaseName)";

    // The post-deployment stamp, exactly: a MERGE into this database's dbo.SchemaVersion (one or two name parts, no server or
    // database), with no WITH clause (a CTE could shadow the table), no OUTPUT, and no DELETE action.
    private static bool IsSchemaVersionStamp(MergeStatement merge) =>
        merge.WithCtesAndXmlNamespaces is null
        && merge.MergeSpecification is { OutputClause: null, OutputIntoClause: null } specification
        && specification.Target is NamedTableReference { SchemaObject: { } target }
        && target.ServerIdentifier is null
        && target.DatabaseIdentifier is null
        && target.BaseIdentifier.Value.Equals("SchemaVersion", StringComparison.OrdinalIgnoreCase)
        && (target.SchemaIdentifier is null || target.SchemaIdentifier.Value.Equals("dbo", StringComparison.OrdinalIgnoreCase))
        && specification.ActionClauses.All(clause => clause.Action is not DeleteMergeAction);

    // GRANT EXECUTE, and nothing else, on one object of this database (OBJECT:: or an unqualified object name; no server or
    // database part, no column list), without WITH GRANT OPTION or AS: it lets the grantees run that routine, and changes no data,
    // schema or other permission.
    private static bool IsExecuteGrant(GrantStatement grant) =>
        !grant.WithGrantOption
        && grant.AsClause is null
        && grant.Permissions.Count == 1
        && grant.Permissions[0].Identifiers.Count == 1
        && grant.Permissions[0].Identifiers[0].Value.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase)
        && grant.Permissions[0].Columns.Count == 0
        && grant.SecurityTargetObject is { ObjectKind: SecurityObjectKind.Object or SecurityObjectKind.NotSpecified, ObjectName.MultiPartIdentifier: { Count: <= 2 } }
        && grant.Principals.Count > 0;

    // A procedure a deploy script may call: a known name, not qualified with a server or database, in the sys schema or unqualified
    // (an unqualified name resolves to the system procedure; [dbo].[sp_refreshsqlmodule] would be someone else's).
    private static bool IsHarmlessProcedure(SchemaObjectName? name) =>
        name is { ServerIdentifier: null, DatabaseIdentifier: null }
        && (name.SchemaIdentifier is null || name.SchemaIdentifier.Value.Equals("sys", StringComparison.OrdinalIgnoreCase))
        && HarmlessProcedures.Contains(name.BaseIdentifier.Value);

    // ReadCommittedSnapshot -> READ_COMMITTED_SNAPSHOT: the spelling a T-SQL author knows.
    private static string OptionName(DatabaseOptionKind kind) => OptionWords().Replace(kind.ToString(), "$1_$2").ToUpperInvariant();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex OptionWords();

    private sealed class Visitor(DeployScriptAnalysis analysis) : TSqlConcreteFragmentVisitor
    {
        // The body of a routine is a definition, not something the script runs: its statements are not inspected.
        public override void ExplicitVisit(CreateProcedureStatement node)
        {
        }

        public override void ExplicitVisit(AlterProcedureStatement node)
        {
        }

        public override void ExplicitVisit(CreateFunctionStatement node)
        {
        }

        public override void ExplicitVisit(AlterFunctionStatement node)
        {
        }

        public override void ExplicitVisit(CreateViewStatement node)
        {
        }

        public override void ExplicitVisit(AlterViewStatement node)
        {
        }
        public override void Visit(TSqlFragment node)
        {
            if (node is TSqlStatement statement)
            {
                Inspect(statement);
            }

            switch (node)
            {
                case AlterTableAddTableElementStatement add:
                    var table = Spell(add.SchemaObjectName);
                    var safe = add.Definition.TableConstraints.Count == 0
                               && add.Definition.Indexes.Count == 0
                               && add.Definition.ColumnDefinitions.Count > 0
                               && add.Definition.ColumnDefinitions.All(IsSafeAddedColumn);
                    analysis._columnAdditionsAreSafe[table] = analysis._columnAdditionsAreSafe.GetValueOrDefault(table, true) && safe;
                    break;

                case AlterTableStatement alter:
                    analysis._otherwiseChanged.Add(Spell(alter.SchemaObjectName));
                    break;

                case DropTableStatement drop:
                    foreach (var dropped in drop.Objects)
                    {
                        analysis._otherwiseChanged.Add(Spell(dropped));
                    }

                    break;

                case ExecuteStatement execute:
                    var procedure = (execute.ExecuteSpecification.ExecutableEntity as ExecutableProcedureReference)
                        ?.ProcedureReference?.ProcedureReference?.Name;
                    if (!IsHarmlessProcedure(procedure))
                    {
                        analysis.HasOpaqueExecution = true;
                    }

                    break;
            }
        }

        private void Inspect(TSqlStatement statement)
        {
            switch (statement)
            {
                case AlterDatabaseSetStatement set:
                    foreach (var option in set.Options)
                    {
                        analysis._databaseOptionChanges.Add(OptionName(option.OptionKind));
                    }

                    break;

                case AlterDatabaseScopedConfigurationSetStatement scoped:
                    analysis._databaseOptionChanges.Add($"DATABASE SCOPED CONFIGURATION {scoped.Option.OptionKind.ToString().ToUpperInvariant()}");
                    break;

                case AlterDatabaseScopedConfigurationStatement scopedOther:
                    analysis._databaseOptionChanges.Add(scopedOther.GetType().Name);
                    break;

                case AlterDatabaseStatement other:
                    analysis._databaseOptionChanges.Add(other.GetType().Name);
                    break;

                case var other when !IsAllowed(other):
                    analysis._disallowedStatements.Add(other.GetType().Name);
                    break;
            }
        }
    }
}