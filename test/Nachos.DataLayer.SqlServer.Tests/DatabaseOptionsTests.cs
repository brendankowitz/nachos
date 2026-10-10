using System.IO.Compression;
using System.Xml.Linq;
using Nachos.DataLayer.SqlServer.Schema;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// Pins the database options each embedded dacpac declares, offline. A deploy to an empty database applies every option the model
/// declares, and the Microsoft.Build.Sql defaults are wrong for a new database (and differ per platform), so an accidental change
/// here would silently reconfigure a production database. DacFx writes only the properties that differ from its platform
/// default, so an absent property means "the platform default"; each test says which default that is.
/// </summary>
public sealed class DatabaseOptionsTests
{
    private static Dictionary<string, string> DeclaredOptions(DacpacTarget target)
    {
        using var stream = target.Open();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var model = archive.GetEntry("model.xml")!.Open();
        var options = XDocument.Load(model).Descendants()
            .Single(element => element.Name.LocalName == "Element" && (string?)element.Attribute("Type") == "SqlDatabaseOptions");

        return options.Elements().Where(property => property.Name.LocalName == "Property")
            .ToDictionary(property => (string)property.Attribute("Name")!, property => (string)property.Attribute("Value")!);
    }

    [Fact]
    public void Azure_DeclaresAzuresOwnDefaults_SoABootstrapSwitchesNothingOff()
    {
        var options = DeclaredOptions(DacpacCatalog.Azure);

        options["IsReadCommittedSnapshot"].ShouldBe("True");
        options["IsAllowSnapshotIsolation"].ShouldBe("True");
        options["QueryStoreMaxStorageSize"].ShouldBe("1024");
        options["TargetRecoveryTimePeriod"].ShouldBe("60");
        options["ServiceBrokerOption"].ShouldBe("1");
        // Absent is the Azure default, MAXDOP 8. The SDK default of 0 is written out explicitly, and would set MAXDOP to 0.
        options.GetValueOrDefault("MaxDop", "8").ShouldBe("8");
        // Absent is the default, CHECKSUM; NONE would be written out.
        options.Keys.ShouldNotContain(key => key.Contains("PageVerify", StringComparison.Ordinal));
    }

    [Fact]
    public void Sql2025_DeclaresASql2025DatabasesDefaults_NotAzures()
    {
        var options = DeclaredOptions(DacpacCatalog.Sql2025);

        options["IsReadCommittedSnapshot"].ShouldBe("True");
        options["QueryStoreMaxStorageSize"].ShouldBe("1000");
        options["TargetRecoveryTimePeriod"].ShouldBe("60");
        options["ServiceBrokerOption"].ShouldBe("1");
        // Box SQL Server has snapshot isolation off and MAXDOP 0 on a new database, and no MAXDOP override is declared.
        options.GetValueOrDefault("IsAllowSnapshotIsolation", "False").ShouldBe("False");
        options.GetValueOrDefault("MaxDop", "0").ShouldBe("0");
        options.Keys.ShouldNotContain(key => key.Contains("PageVerify", StringComparison.Ordinal));
    }
}
