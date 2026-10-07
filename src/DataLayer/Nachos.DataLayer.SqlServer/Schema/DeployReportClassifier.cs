using System.Xml;
using System.Xml.Linq;
using Nachos.Abstractions.Schema;

namespace Nachos.DataLayer.SqlServer.Schema;

/// <summary>
/// Decides whether a DacFx deploy report (<c>DacServices.GenerateDeployReport</c>) may be applied without review.
/// </summary>
/// <remarks>
/// Fails closed. A report is <see cref="DeployClassification.AutoSafe"/> only when it has no <c>Alert</c> elements
/// and every <c>Operation</c>/<c>Item</c> pair is on the allowlist below. The report format, as DacFx 170 writes it:
/// <c>DeploymentReport/Alerts/Alert[@Name]</c> (<c>DataIssue</c>, <c>DataMotion</c>) and
/// <c>DeploymentReport/Operations/Operation[@Name]/Item[@Value,@Type]</c> (the <c>Operations</c> element is omitted when there is nothing to do), with operation names
/// <c>Create</c>, <c>Alter</c>, <c>Drop</c> and <c>TableRebuild</c>.
/// Adding a column to an existing table is reported as <c>Alter</c> of the <c>SqlTable</c>, which is
/// indistinguishable from a column type or nullability change, so it is not auto-safe.
/// </remarks>
public static class DeployReportClassifier
{
    private static readonly XNamespace Report = "http://schemas.microsoft.com/sqlserver/dac/DeployReport/2012/02";

    private static readonly HashSet<string> RoutineTypes =
    [
        "SqlProcedure", "SqlView", "SqlScalarFunction", "SqlInlineTableValuedFunction", "SqlMultiStatementTableValuedFunction",
    ];

    private static readonly HashSet<string> CreatableTypes =
    [
        "SqlTable", "SqlSimpleColumn", "SqlIndex",
        "SqlPrimaryKeyConstraint", "SqlDefaultConstraint", "SqlCheckConstraint", "SqlUniqueConstraint", "SqlForeignKeyConstraint",
        .. RoutineTypes,
    ];

    /// <summary>Classifies the report. Never throws: anything that is not a recognisable report is <see cref="DeployClassification.Unclassifiable"/>.</summary>
    public static DeployClassification Classify(string reportXml)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(reportXml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return DeployClassification.Unclassifiable;
        }

        var root = document.Root;
        if (root is null || root.Name != Report + "DeploymentReport" ||
            root.Element(Report + "Alerts") is not { } alerts)
        {
            return DeployClassification.Unclassifiable;
        }

        var verdict = alerts.Elements(Report + "Alert").Any() ? DeployClassification.Unsafe : DeployClassification.AutoSafe;

        foreach (var operation in root.Elements(Report + "Operations").Elements(Report + "Operation"))
        {
            if (operation.Attribute("Name")?.Value is not { } name)
            {
                return DeployClassification.Unclassifiable;
            }

            foreach (var item in operation.Elements(Report + "Item"))
            {
                if (item.Attribute("Type")?.Value is not { } type)
                {
                    return DeployClassification.Unclassifiable;
                }

                switch (Classify(name, type))
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

    private static DeployClassification Classify(string operation, string type) => operation switch
    {
        "Create" when CreatableTypes.Contains(type) => DeployClassification.AutoSafe,
        "Alter" when RoutineTypes.Contains(type) => DeployClassification.AutoSafe,
        "Alter" when type == "SqlTable" => DeployClassification.Unsafe,
        "Drop" or "TableRebuild" => DeployClassification.Unsafe,
        _ => DeployClassification.Unclassifiable,
    };
}