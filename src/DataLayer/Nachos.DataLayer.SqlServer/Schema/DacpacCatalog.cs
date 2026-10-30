using System.Collections.Concurrent;
using System.IO.Compression;
using System.Xml;
using Microsoft.SqlServer.Dac.Model;

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

/// <summary>Facts about a dacpac's model that a DeployReport does not carry.</summary>
internal static class DacpacModel
{
    private static readonly ConcurrentDictionary<string, Lazy<IReadOnlyDictionary<string, string>>> ConstraintCache = new();
    private static readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<string>>> TableCache = new();

    /// <summary>
    /// Maps each constraint's name (<c>[dbo].[CK_x]</c>, as a DeployReport item spells it) to the table that owns it
    /// (<c>[dbo].[Sessions]</c>). A report item for a constraint names no table, so the classifier needs this to know
    /// whether a constraint is created together with its table or added to existing data.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ConstraintTables(DacpacTarget target) =>
        ConstraintCache.GetOrAdd(target.ResourceName, _ => new Lazy<IReadOnlyDictionary<string, string>>(() => Load(target))).Value;

    /// <summary>
    /// The name of every <c>dbo</c> table the model defines, ordinal-sorted. Read straight from the embedded dacpac's
    /// <c>model.xml</c> (no temp file, no DacFx model) and kept for the life of the process; a failed read is not remembered.
    /// It covers table presence only: columns, constraints and other schemas are the deploy report's job.
    /// </summary>
    public static IReadOnlyList<string> TableNames(DacpacTarget target) =>
        TableCache.GetOrAdd(
            target.ResourceName,
            _ => new Lazy<IReadOnlyList<string>>(() => ReadTableNames(target), LazyThreadSafetyMode.PublicationOnly)).Value;

    private static string[] ReadTableNames(DacpacTarget target)
    {
        const string DboPrefix = "[dbo].[";
        using var archive = new ZipArchive(target.Open(), ZipArchiveMode.Read);
        var entry = archive.GetEntry("model.xml")
            ?? throw new InvalidOperationException($"Embedded dacpac '{target.ResourceName}' has no model.xml.");
        using var stream = entry.Open();

        var names = new List<string>();
        using var reader = XmlReader.Create(stream);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element
                && reader.LocalName == "Element"
                && reader.GetAttribute("Type") == "SqlTable"
                && reader.GetAttribute("Name") is { } name
                && name.StartsWith(DboPrefix, StringComparison.Ordinal)
                && name.EndsWith(']'))
            {
                names.Add(name[DboPrefix.Length..^1]);
            }
        }

        names.Sort(StringComparer.Ordinal);
        return [.. names];
    }

    private static Dictionary<string, string> Load(DacpacTarget target) =>
        WithModel(target, model => LoadConstraintTables(model, target));

    private static T WithModel<T>(DacpacTarget target, Func<TSqlModel, T> read)
    {
        // TSqlModel reads from a file path only.
        var path = Path.Combine(Path.GetTempPath(), $"nachos-model-{Guid.NewGuid():N}.dacpac");
        try
        {
            using (var source = target.Open())
            using (var file = File.Create(path))
            {
                source.CopyTo(file);
            }

            using var model = new TSqlModel(path);
            return read(model);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Dictionary<string, string> LoadConstraintTables(TSqlModel model, DacpacTarget target)
    {
        var tables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var constraint in model.GetObjects(
                     DacQueryScopes.UserDefined,
                     ModelSchema.PrimaryKeyConstraint,
                     ModelSchema.DefaultConstraint,
                     ModelSchema.CheckConstraint,
                     ModelSchema.UniqueConstraint,
                     ModelSchema.ForeignKeyConstraint))
        {
            var parent = constraint.GetParent()
                ?? throw new InvalidOperationException($"Constraint {constraint.Name} in {target.ResourceName} has no parent object.");
            tables[constraint.Name.ToString()] = parent.Name.ToString();
        }

        return tables;
    }
}