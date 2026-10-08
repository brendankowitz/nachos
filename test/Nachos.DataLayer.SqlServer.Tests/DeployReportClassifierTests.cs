using Nachos.Abstractions.Schema;
using Nachos.DataLayer.SqlServer.Schema;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// Pure tests over DeployReport XML. The report-*.xml and script-*.sql fixtures were captured from DacFx against SQL Server
/// 2025 (or trimmed from such a capture, which their header says); the model's constraint owners come from the real dacpac.
/// </summary>
public sealed class DeployReportClassifierTests
{
    private const string Ns = "http://schemas.microsoft.com/sqlserver/dac/DeployReport/2012/02";

    private static readonly IReadOnlyDictionary<string, string> ConstraintTables = DacpacModel.ConstraintTables(DacpacCatalog.Sql2025);

    private static DeployClassification Classify(string fixtureOrXml, string? scriptFixture = null) =>
        DeployReportClassifier.Classify(
            fixtureOrXml.StartsWith('<') || fixtureOrXml.Length == 0 || !fixtureOrXml.EndsWith(".xml", StringComparison.Ordinal) ? fixtureOrXml : Fixtures.Read(fixtureOrXml),
            ConstraintTables,
            () => scriptFixture is null
                ? throw new InvalidOperationException("The deploy script must not be analysed for this report.")
                : DeployScriptAnalysis.TryParse(scriptFixture.EndsWith(".sql", StringComparison.Ordinal) ? Fixtures.Read(scriptFixture) : scriptFixture));

    private static string Report(string body) => $"""<DeploymentReport xmlns="{Ns}">{body}</DeploymentReport>""";

    [Fact]
    public void AllowlistedCreatesNoAlerts_IsAutoSafe()
    {
        // Every constraint here belongs to a table the same report creates.
        Classify("report-empty-database.xml").ShouldBe(DeployClassification.AutoSafe);
    }

    [Fact]
    public void AllowlistedRoutineCreatesAndAlters_AreAutoSafe()
    {
        Classify("report-routines-allowlisted.xml").ShouldBe(DeployClassification.AutoSafe);
    }

    [Fact]
    public void NoChanges_ReportWithoutOperationsElement_IsAutoSafe()
    {
        // DacFx omits <Operations> entirely when the database already matches the model.
        Classify("report-no-changes.xml").ShouldBe(DeployClassification.AutoSafe);
    }

    [Fact]
    public void EmptyOperationsElementNoAlerts_IsAutoSafe()
    {
        Classify(Report("<Alerts /><Operations />")).ShouldBe(DeployClassification.AutoSafe);
    }

    [Theory]
    [InlineData("report-extra-column-data-issue.xml")]
    [InlineData("report-alter-column-type-narrowing.xml")]
    [InlineData("report-table-rebuild-data-motion.xml")]
    [InlineData("report-add-not-null-column-without-default.xml")]
    public void DataIssueAlert_IsUnsafe(string fixture)
    {
        // The alert alone decides: the deploy script is not needed.
        Classify(fixture).ShouldBe(DeployClassification.Unsafe);
    }

    [Theory]
    [InlineData("report-drop-only.xml")]
    [InlineData("report-table-rebuild-only.xml")]
    public void DropOrRebuildWithoutAlerts_IsUnsafe(string fixture)
    {
        Classify(fixture).ShouldBe(DeployClassification.Unsafe);
    }

    [Theory]
    [InlineData("report-constraint-on-existing-table.xml")]
    [InlineData("report-default-on-existing-table.xml")]
    public void ConstraintCreateOnExistingTable_IsUnsafe(string fixture)
    {
        // A CHECK added to a table with rows is scripted WITH NOCHECK and left untrusted, or fails the deploy half-way.
        Classify(fixture).ShouldBe(DeployClassification.Unsafe);
    }

    [Fact]
    public void ConstraintNotInTheModel_IsUnclassifiable()
    {
        const string xml = """<Alerts /><Operations><Operation Name="Create"><Item Value="[dbo].[CK_Nobody_Knows]" Type="SqlCheckConstraint" /></Operation></Operations>""";

        Classify(Report(xml)).ShouldBe(DeployClassification.Unclassifiable);
    }

    [Fact]
    public void AddNullableColumn_IsAutoSafe()
    {
        Classify("report-add-nullable-column.xml", "script-add-nullable-column.sql").ShouldBe(DeployClassification.AutoSafe);
    }

    [Fact]
    public void AddNotNullColumnWithDefault_IsAutoSafe()
    {
        Classify("report-add-not-null-column-with-default.xml", "script-add-not-null-column-with-default.sql").ShouldBe(DeployClassification.AutoSafe);
    }

    [Fact]
    public void AlterColumnNullability_IsUnsafe_EvenWithoutAnAlert()
    {
        // The report says only "Alter SqlTable", the same as for the harmless add-column case; the script tells them apart.
        Classify("report-alter-column-nullability.xml", "script-alter-column-nullability.sql").ShouldBe(DeployClassification.Unsafe);
    }

    [Fact]
    public void AlterTable_WithUnparseableScript_IsUnclassifiable()
    {
        Classify("report-add-nullable-column.xml", "this is not T-SQL at all").ShouldBe(DeployClassification.Unclassifiable);
    }

    [Fact]
    public void AlterTable_ScriptThatNeverMentionsTheTable_IsUnclassifiable()
    {
        Classify("report-add-nullable-column.xml", "PRINT N'nothing to see';").ShouldBe(DeployClassification.Unclassifiable);
    }

    [Theory]
    [InlineData("report-unknown-operation.xml")]
    [InlineData("report-unknown-object-type.xml")]
    public void UnknownOperationWithoutAlerts_IsUnclassifiable(string fixture)
    {
        Classify(fixture).ShouldBe(DeployClassification.Unclassifiable);
    }

    [Theory]
    [InlineData("<DeploymentReport xmlns=\"http://schemas.microsoft.com/sqlserver/dac/DeployReport/2012/02\"><Alerts>")]
    [InlineData("")]
    [InlineData("not xml at all")]
    public void MalformedXml_IsUnclassifiable(string xml)
    {
        Classify(xml).ShouldBe(DeployClassification.Unclassifiable);
    }

    [Theory]
    [InlineData("<html/>")]
    [InlineData("""<DeploymentReport xmlns="urn:other"><Alerts /><Operations /></DeploymentReport>""")]
    [InlineData("""<DeploymentReport xmlns="http://schemas.microsoft.com/sqlserver/dac/DeployReport/2012/02" />""")]
    public void WellFormedButNotADeployReport_IsUnclassifiable(string xml)
    {
        Classify(xml).ShouldBe(DeployClassification.Unclassifiable);
    }

    [Theory]
    [InlineData("<Alerts /><Surprise />")]
    [InlineData("<Alerts><Surprise /></Alerts>")]
    [InlineData("<Alerts><Alert><Issue /></Alert></Alerts>")]
    [InlineData("<Alerts><Alert Name=\"DataIssue\"><Surprise /></Alert></Alerts>")]
    [InlineData("<Operations />")]
    [InlineData("<Alerts /><Operations><Surprise /></Operations>")]
    [InlineData("<Alerts /><Operations><Operation Name=\"Create\" /></Operations>")]
    [InlineData("<Alerts /><Operations><Operation><Item Value=\"x\" Type=\"SqlTable\" /></Operation></Operations>")]
    [InlineData("<Alerts /><Operations><Operation Name=\"Create\"><Surprise /></Operation></Operations>")]
    [InlineData("<Alerts /><Operations><Operation Name=\"Create\"><Item Type=\"SqlTable\" /></Operation></Operations>")]
    [InlineData("<Alerts /><Operations><Operation Name=\"Create\"><Item Value=\"x\" /></Operation></Operations>")]
    [InlineData("<Alerts /><Operations><Operation Name=\"Create\"><Item Value=\"x\" Type=\"SqlTable\"><Surprise /></Item></Operation></Operations>")]
    public void UnexpectedStructure_IsUnclassifiable(string body)
    {
        Classify(Report(body)).ShouldBe(DeployClassification.Unclassifiable);
    }

    [Fact]
    public void AlertInAnyAlertsElement_IsUnsafe()
    {
        const string body = """<Alerts /><Alerts><Alert Name="DataIssue"><Issue Value="x" /></Alert></Alerts>""";

        Classify(Report(body)).ShouldBe(DeployClassification.Unsafe);
    }

    [Fact]
    public void DoctypeInReport_IsUnclassifiable()
    {
        const string xml = """<!DOCTYPE r [<!ENTITY e "x">]><DeploymentReport xmlns="http://schemas.microsoft.com/sqlserver/dac/DeployReport/2012/02"><Alerts /><Operations /></DeploymentReport>""";

        Classify(xml).ShouldBe(DeployClassification.Unclassifiable);
    }

    [Theory]
    [InlineData("report-no-changes.xml", false)]
    [InlineData("report-empty-database.xml", true)]
    [InlineData("report-drop-only.xml", true)]
    public void HasOperations_ReflectsTheReport(string fixture, bool expected)
    {
        DeployReportClassifier.HasOperations(Fixtures.Read(fixture)).ShouldBe(expected);
    }

    [Fact]
    public void HasOperations_UnreadableReport_CountsAsHavingSome()
    {
        DeployReportClassifier.HasOperations("not xml").ShouldBeTrue();
    }
}