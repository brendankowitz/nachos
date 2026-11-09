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

    /// <summary>How often <c>Search.FullTextIndexMaintenance</c> checks the full-text index of message search.</summary>
    public TimeSpan FullTextIndexPollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A test seam of the spec §13.1 race tests: awaited by deletion acceptance after its predicate check and before the
    /// lifecycle transition, with the acceptance's locks held. Internal, so configuration binding never sets it.
    /// </summary>
    internal Func<CancellationToken, Task>? BeforeLifecycleTransition { get; set; }
}