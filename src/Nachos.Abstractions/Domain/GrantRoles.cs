namespace Nachos.Abstractions.Domain;

/// <summary>The role names stored in <see cref="GrantRecord.Role"/>.</summary>
public static class GrantRoles
{
    /// <summary>Administrative access; contributes nothing to <see cref="WorkspaceGrants"/>.</summary>
    public const string Admin = "Nachos.Admin";

    /// <summary>Access to one workspace, or to every workspace when <see cref="GrantRecord.WorkspaceName"/> is null.</summary>
    public const string Workspace = "Nachos.Workspace";
}