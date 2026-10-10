namespace Nachos.Abstractions.Domain;

/// <summary>The workspaces an object may access, derived from its <see cref="GrantRoles.Workspace"/> grants.</summary>
/// <param name="AllWorkspaces">True when a <see cref="GrantRoles.Workspace"/> grant has no workspace name.</param>
/// <param name="Workspaces">The distinct workspace names granted explicitly.</param>
public sealed record WorkspaceGrants(bool AllWorkspaces, IReadOnlySet<string> Workspaces);