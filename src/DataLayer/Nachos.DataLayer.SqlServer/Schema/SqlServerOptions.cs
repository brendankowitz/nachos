namespace Nachos.DataLayer.SqlServer.Schema;

/// <summary>Settings of the SQL Server provider, bound from configuration section <see cref="SectionName"/>.</summary>
public sealed class SqlServerOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Nachos:SqlServer";

    /// <summary>The connection string of the Nachos database.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// When true, the first data access may create the schema in an empty database and apply auto-safe upgrades.
    /// Everything else always needs <c>nachos schema upgrade</c>.
    /// </summary>
    public bool AutomaticSchemaDeploymentEnabled { get; set; }
}