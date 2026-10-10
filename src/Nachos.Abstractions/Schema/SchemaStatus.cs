namespace Nachos.Abstractions.Schema;

/// <summary>A database's schema position.</summary>
/// <param name="Platform">The dacpac platform chosen for the server (for example <c>Azure</c> or <c>Sql2025</c>).</param>
/// <param name="Deployed">The stamped version, or null when the database is empty or unstamped.</param>
/// <param name="Current">The version this build of Nachos expects.</param>
/// <param name="State">The comparison of the two.</param>
public sealed record SchemaStatus(string Platform, int? Deployed, int Current, SchemaState State);