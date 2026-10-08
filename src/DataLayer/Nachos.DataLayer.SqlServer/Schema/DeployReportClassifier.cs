using System.Xml;
using System.Xml.Linq;
using Nachos.Abstractions.Schema;

namespace Nachos.DataLayer.SqlServer.Schema;

/// <summary>
/// Decides whether a DacFx deploy report (<c>DacServices.GenerateDeployReport</c>) may be applied without review.
/// </summary>
/// <remarks>
/// Fails closed. The report must have exactly the structure DacFx 170 writes, and is
/// <see cref="DeployClassification.AutoSafe"/> only when it has no <c>Alert</c> and every <c>Operation</c>/<c>Item</c>
/// pair is on the allowlist below:
/// <list type="bullet">
/// <item><c>Create</c> of a table, column, index, procedure, function or view.</item>
/// <item><c>Create</c> of a constraint only when its table is created in the same report: a constraint added to a
/// table that already has rows is validated against them, or scripted <c>WITH NOCHECK</c> and left untrusted.</item>
/// <item><c>Alter</c> of a procedure, function or view.</item>
/// <item><c>Alter</c> of a table only when the generated script merely adds nullable or defaulted columns to it
/// (see <see cref="DeployScriptAnalysis"/>); the report itself cannot tell that from a type change.</item>
/// </list>
/// Drops and table rebuilds are <see cref="DeployClassification.Unsafe"/>; an unrecognised operation or object type,
/// or a report that is not well-formed, is <see cref="DeployClassification.Unclassifiable"/>.
/// Format: <c>DeploymentReport/Alerts/Alert[@Name]/Issue</c> and
/// <c>DeploymentReport/Operations/Operation[@Name]/Item[@Value,@Type]/Issue</c>; the <c>Operations</c> element is
/// omitted when there is nothing to do. Operation names seen: <c>Create</c>, <c>Alter</c>, <c>Drop</c>, <c>TableRebuild</c>.
/// </remarks>
internal static class DeployReportClassifier
{
    private static readonly XNamespace Report = "http://schemas.microsoft.com/sqlserver/dac/DeployReport/2012/02";

    private static readonly HashSet<string> RoutineTypes =
    [
        "SqlProcedure", "SqlView", "SqlScalarFunction", "SqlInlineTableValuedFunction", "SqlMultiStatementTableValuedFunction",
    ];

    private static readonly HashSet<string> ConstraintTypes =
    [
        "SqlPrimaryKeyConstraint", "SqlDefaultConstraint", "SqlCheckConstraint", "SqlUniqueConstraint", "SqlForeignKeyConstraint",
    ];

    private static readonly HashSet<string> OtherCreatableTypes = ["SqlTable", "SqlSimpleColumn", "SqlIndex", .. RoutineTypes];

    private sealed record Item(string Type, string Value);

    private sealed record Operation(string Name, IReadOnlyList<Item> Items);

    private sealed record ParsedReport(bool HasAlerts, IReadOnlyList<Operation> Operations);

    /// <summary>
    /// Classifies the report. Never throws for bad input: anything that is not a recognisable report is
    /// <see cref="DeployClassification.Unclassifiable"/>.
    /// </summary>
    /// <param name="reportXml">The DeployReport.</param>
    /// <param name="constraintTables">Constraint name to owning table, from the dacpac model (<see cref="DacpacModel"/>).</param>
    /// <param name="deployScript">The analysed deploy script, or null if it cannot be parsed; called at most once, and only when a table is altered.</param>
    public static DeployClassification Classify(
        string reportXml,
        IReadOnlyDictionary<string, string> constraintTables,
        Func<DeployScriptAnalysis?> deployScript)
    {
        if (Parse(reportXml) is not { } report)
        {
            return DeployClassification.Unclassifiable;
        }

        var createdTables = report.Operations
            .Where(o => o.Name == "Create")
            .SelectMany(o => o.Items)
            .Where(i => i.Type == "SqlTable")
            .Select(i => i.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var verdict = report.HasAlerts ? DeployClassification.Unsafe : DeployClassification.AutoSafe;
        Lazy<DeployScriptAnalysis?> script = new(deployScript);

        foreach (var operation in report.Operations)
        {
            foreach (var item in operation.Items)
            {
                switch (Classify(operation.Name, item, createdTables, constraintTables, script, report.HasAlerts))
                {
                    case DeployClassification.Unclassifiable:
                        return DeployClassification.Unclassifiable;
                    case DeployClassification.Unsafe:
                        verdict = DeployClassification.Unsafe;
                        break;
                }
            }
        }

        return verdict;
    }

    /// <summary>True when the report lists any operation. A report that cannot be read counts as having some.</summary>
    public static bool HasOperations(string reportXml) => Parse(reportXml) is not { } report || report.Operations.Count > 0;

    private static DeployClassification Classify(
        string operation,
        Item item,
        HashSet<string> createdTables,
        IReadOnlyDictionary<string, string> constraintTables,
        Lazy<DeployScriptAnalysis?> script,
        bool reportHasAlerts)
    {
        switch (operation)
        {
            case "Create" when OtherCreatableTypes.Contains(item.Type):
                return DeployClassification.AutoSafe;

            case "Create" when ConstraintTypes.Contains(item.Type):
                return constraintTables.TryGetValue(item.Value, out var table)
                    ? createdTables.Contains(table) ? DeployClassification.AutoSafe : DeployClassification.Unsafe
                    : DeployClassification.Unclassifiable;

            case "Alter" when RoutineTypes.Contains(item.Type):
                return DeployClassification.AutoSafe;

            case "Alter" when item.Type == "SqlTable":
                // The alert already makes the report unsafe, and the script of a failing change proves nothing more.
                return reportHasAlerts
                    ? DeployClassification.Unsafe
                    : script.Value?.ClassifyTable(item.Value) ?? DeployClassification.Unclassifiable;

            case "Drop" or "TableRebuild":
                return DeployClassification.Unsafe;

            default:
                return DeployClassification.Unclassifiable;
        }
    }

    // Returns null for anything that is not exactly the format described on the class.
    private static ParsedReport? Parse(string reportXml)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(reportXml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        if (document.Root is not { } root || root.Name != Report + "DeploymentReport")
        {
            return null;
        }

        var allowedRootChildren = new[] { Report + "Alerts", Report + "Operations" };
        if (root.Elements().Any(e => !allowedRootChildren.Contains(e.Name)) || !root.Elements(Report + "Alerts").Any())
        {
            return null;
        }

        var alerts = root.Elements(Report + "Alerts").SelectMany(a => a.Elements()).ToList();
        if (alerts.Any(a => a.Name != Report + "Alert" || a.Attribute("Name") is null || !OnlyChildren(a, "Issue")))
        {
            return null;
        }

        var operations = new List<Operation>();
        foreach (var element in root.Elements(Report + "Operations").SelectMany(o => o.Elements()))
        {
            if (element.Name != Report + "Operation" || element.Attribute("Name")?.Value is not { } name || !element.HasElements)
            {
                return null;
            }

            var items = new List<Item>();
            foreach (var child in element.Elements())
            {
                if (child.Name != Report + "Item" ||
                    child.Attribute("Type")?.Value is not { } type ||
                    child.Attribute("Value")?.Value is not { } value ||
                    !OnlyChildren(child, "Issue"))
                {
                    return null;
                }

                items.Add(new Item(type, value));
            }

            operations.Add(new Operation(name, items));
        }

        return new ParsedReport(alerts.Count > 0, operations);
    }

    private static bool OnlyChildren(XElement element, string localName) =>
        element.Elements().All(e => e.Name == Report + localName);
}