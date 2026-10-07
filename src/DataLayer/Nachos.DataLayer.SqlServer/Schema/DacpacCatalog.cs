using System.Reflection;

namespace Nachos.DataLayer.SqlServer.Schema;

/// <summary>The dacpac built for one database platform, embedded in this assembly.</summary>
/// <param name="Platform">Platform name reported by <c>GetStatusAsync</c>.</param>
/// <param name="ResourceName">Manifest resource name of the embedded dacpac.</param>
internal sealed record DacpacTarget(string Platform, string ResourceName)
{
    public Stream Open() =>
        typeof(DacpacTarget).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException($"Embedded dacpac '{ResourceName}' is missing from {typeof(DacpacTarget).Assembly.GetName().Name}.");
}

/// <summary>Chooses the embedded dacpac for a server. Never overrides platform compatibility: a dacpac is only ever deployed to the platform it was built for.</summary>
internal static class DacpacCatalog
{
    private const int AzureSqlDatabaseEngineEdition = 5;
    private const int FirstSql2025MajorVersion = 17;

    public static readonly DacpacTarget Azure = new("Azure", "Nachos.Azure.dacpac");
    public static readonly DacpacTarget Sql2025 = new("Sql2025", "Nachos.Sql2025.dacpac");

    /// <exception cref="NotSupportedException">Managed Instance, older box SQL Server, or an unknown engine edition.</exception>
    public static DacpacTarget Select(int engineEdition, int productMajorVersion) =>
        engineEdition switch
        {
            AzureSqlDatabaseEngineEdition => Azure,
            >= 1 and <= 4 when productMajorVersion >= FirstSql2025MajorVersion => Sql2025,
            _ => throw new NotSupportedException(
                $"Nachos supports Azure SQL Database (EngineEdition 5) and SQL Server 2025 or later (EngineEdition 1-4, major version {FirstSql2025MajorVersion}+). " +
                $"This server reports EngineEdition {engineEdition} and major version {productMajorVersion}; Azure SQL Managed Instance and older SQL Server are not supported."),
        };
}