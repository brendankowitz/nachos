using Nachos.Abstractions.Schema;
using Nachos.DataLayer.SqlServer.Schema;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>Pure tests over DeployReport XML. The report-*.xml fixtures were captured from DacFx against SQL Server 2025 unless their header says otherwise.</summary>
public sealed class DeployReportClassifierTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void AllowlistedCreatesNoAlerts_IsAutoSafe()
    {
        DeployReportClassifier.Classify(Fixture("report-empty-database.xml")).ShouldBe(DeployClassification.AutoSafe);
    }

    [Fact]
    public void AllowlistedRoutineCreatesAndAlters_AreAutoSafe()
    {
        DeployReportClassifier.Classify(Fixture("report-routines-allowlisted.xml")).ShouldBe(DeployClassification.AutoSafe);
    }

    [Fact]
    public void NoChanges_ReportWithoutOperationsElement_IsAutoSafe()
    {
        // DacFx omits <Operations> entirely when the database already matches the model.
        DeployReportClassifier.Classify(Fixture("report-no-changes.xml")).ShouldBe(DeployClassification.AutoSafe);
    }

    [Fact]
    public void NoOperationsNoAlerts_IsAutoSafe()
    {
        const string xml = """<DeploymentReport xmlns="http://schemas.microsoft.com/sqlserver/dac/DeployReport/2012/02"><Alerts /><Operations /></DeploymentReport>""";

        DeployReportClassifier.Classify(xml).ShouldBe(DeployClassification.AutoSafe);
    }

    [Theory]
    [InlineData("report-extra-column-data-issue.xml")]
    [InlineData("report-column-type-change.xml")]
    [InlineData("report-table-rebuild-data-motion.xml")]
    public void DataIssueAlert_IsUnsafe(string fixture)
    {
        DeployReportClassifier.Classify(Fixture(fixture)).ShouldBe(DeployClassification.Unsafe);
    }

    [Theory]
    [InlineData("report-check-constraint-recreate.xml")]
    [InlineData("report-table-rebuild-without-alert.xml")]
    public void DropOrRebuildWithoutAlerts_IsUnsafe(string fixture)
    {
        DeployReportClassifier.Classify(Fixture(fixture)).ShouldBe(DeployClassification.Unsafe);
    }

    [Fact]
    public void AlterTable_IsUnsafe_BecauseReportCannotDistinguishAddColumnFromTypeChange()
    {
        DeployReportClassifier.Classify(Fixture("report-alter-table-add-column.xml")).ShouldBe(DeployClassification.Unsafe);
    }

    [Theory]
    [InlineData("report-unknown-operation.xml")]
    [InlineData("report-unknown-object-type.xml")]
    public void UnknownOperationWithoutAlerts_IsUnclassifiable(string fixture)
    {
        DeployReportClassifier.Classify(Fixture(fixture)).ShouldBe(DeployClassification.Unclassifiable);
    }

    [Theory]
    [InlineData("<DeploymentReport xmlns=\"http://schemas.microsoft.com/sqlserver/dac/DeployReport/2012/02\"><Alerts>")]
    [InlineData("")]
    [InlineData("not xml at all")]
    public void MalformedXml_IsUnclassifiable(string xml)
    {
        DeployReportClassifier.Classify(xml).ShouldBe(DeployClassification.Unclassifiable);
    }

    [Theory]
    [InlineData("<html/>")]
    [InlineData("""<DeploymentReport xmlns="urn:other"><Alerts /><Operations /></DeploymentReport>""")]
    [InlineData("""<DeploymentReport xmlns="http://schemas.microsoft.com/sqlserver/dac/DeployReport/2012/02" />""")]
    public void WellFormedButNotADeployReport_IsUnclassifiable(string xml)
    {
        DeployReportClassifier.Classify(xml).ShouldBe(DeployClassification.Unclassifiable);
    }

    [Fact]
    public void OperationWithoutItsAttributes_IsUnclassifiable()
    {
        const string xml = """<DeploymentReport xmlns="http://schemas.microsoft.com/sqlserver/dac/DeployReport/2012/02"><Alerts /><Operations><Operation><Item Value="x" Type="SqlTable" /></Operation></Operations></DeploymentReport>""";

        DeployReportClassifier.Classify(xml).ShouldBe(DeployClassification.Unclassifiable);
    }

    [Fact]
    public void DoctypeInReport_IsUnclassifiable()
    {
        const string xml = """<!DOCTYPE r [<!ENTITY e "x">]><DeploymentReport xmlns="http://schemas.microsoft.com/sqlserver/dac/DeployReport/2012/02"><Alerts /><Operations /></DeploymentReport>""";

        DeployReportClassifier.Classify(xml).ShouldBe(DeployClassification.Unclassifiable);
    }
}