using Shouldly;
using Nachos.DataLayer.SqlServer.Schema;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>Platform selection and the dacpac model facts, without a database.</summary>
public sealed class DacpacCatalogTests
{
    [Theory]
    [InlineData(5, 12, "Azure")]
    [InlineData(5, 0, "Azure")]
    [InlineData(3, 17, "Sql2025")]
    [InlineData(4, 17, "Sql2025")]
    [InlineData(2, 18, "Sql2025")]
    public void SupportedPlatforms_SelectTheirDacpac(int engineEdition, int majorVersion, string platform)
    {
        DacpacCatalog.Select(engineEdition, majorVersion).Platform.ShouldBe(platform);
    }

    [Theory]
    [InlineData(8, 16)] // Managed Instance
    [InlineData(8, 17)]
    [InlineData(3, 16)] // SQL Server 2022
    [InlineData(2, 16)]
    [InlineData(6, 0)]  // an engine edition this code has never heard of
    [InlineData(0, 17)]
    public void EverythingElse_IsRefusedWithAClearMessage(int engineEdition, int majorVersion)
    {
        var failure = Should.Throw<NotSupportedException>(() => DacpacCatalog.Select(engineEdition, majorVersion));

        failure.Message.ShouldContain($"EngineEdition {engineEdition}");
    }

    [Theory]
    [InlineData("Azure")]
    [InlineData("Sql2025")]
    public void BothEmbeddedDacpacsOpen(string platform)
    {
        var target = platform == "Azure" ? DacpacCatalog.Azure : DacpacCatalog.Sql2025;

        using var stream = target.Open();

        stream.Length.ShouldBeGreaterThan(1000);
    }

    [Theory]
    [InlineData("Azure")]
    [InlineData("Sql2025")]
    public void ConstraintTables_NamesTheOwningTable(string platform)
    {
        var target = platform == "Azure" ? DacpacCatalog.Azure : DacpacCatalog.Sql2025;

        var constraints = DacpacModel.ConstraintTables(target);

        constraints["[dbo].[CK_Sessions_LifecycleState]"].ShouldBe("[dbo].[Sessions]");
        constraints["[dbo].[FK_Messages_Peers]"].ShouldBe("[dbo].[Messages]");
        constraints["[dbo].[DF_SchemaVersion_AppliedAt]"].ShouldBe("[dbo].[SchemaVersion]");
        constraints["[dbo].[PK_Workspaces]"].ShouldBe("[dbo].[Workspaces]");
        constraints["[dbo].[UQ_Messages_PublicId]"].ShouldBe("[dbo].[Messages]");
        constraints.Count.ShouldBeGreaterThan(40);
    }
}