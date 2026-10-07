using Microsoft.SqlServer.TransactSql.ScriptDom;
using Nachos.Abstractions.Schema;

namespace Nachos.DataLayer.SqlServer.Schema;

/// <summary>
/// Reads the script DacFx generates for a deploy, to tell what an <c>Alter</c> of a table in the DeployReport really does:
/// the report names the table but not the change, and adding a nullable column looks identical to narrowing a type.
/// </summary>
/// <remarks>
/// Fails closed. A table is <see cref="DeployClassification.AutoSafe"/> only when the script touches it with
/// <c>ALTER TABLE … ADD</c> of plain columns that are nullable or have a <c>DEFAULT</c>, and nothing else. A script
/// that does not parse, or that runs dynamic SQL, proves nothing, so its tables are
/// <see cref="DeployClassification.Unclassifiable"/>.
/// </remarks>
internal sealed class DeployScriptAnalysis
{
    // Procedures a deploy script may call without changing what it does to a table's columns.
    private static readonly HashSet<string> HarmlessProcedures = new(StringComparer.OrdinalIgnoreCase)
    {
        "sp_refreshsqlmodule", "sp_refreshview",
    };

    private readonly Dictionary<string, bool> _columnAdditionsAreSafe = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _otherwiseChanged = new(StringComparer.OrdinalIgnoreCase);
    private bool _opaque;

    private DeployScriptAnalysis()
    {
    }

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
        if (_opaque)
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

    private sealed class Visitor(DeployScriptAnalysis analysis) : TSqlConcreteFragmentVisitor
    {
        public override void Visit(TSqlFragment node)
        {
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
                        ?.ProcedureReference?.ProcedureReference?.Name?.BaseIdentifier?.Value;
                    if (procedure is null || !HarmlessProcedures.Contains(procedure))
                    {
                        analysis._opaque = true;
                    }

                    break;
            }
        }
    }
}