using Nachos.Abstractions.Work;

namespace Nachos.Abstractions.Domain;

/// <summary>The resource a deletion job removes: a session, or a whole workspace.</summary>
public sealed record DeletionTarget
{
    private DeletionTarget(DeletionTargetKind kind, string workspaceName, string? sessionName)
    {
        Kind = kind;
        WorkspaceName = workspaceName;
        SessionName = sessionName;
    }

    public DeletionTargetKind Kind { get; }

    public string WorkspaceName { get; }

    /// <summary>The session's name for a session target; null for a workspace target.</summary>
    public string? SessionName { get; }

    /// <summary>The target's own name: the session's, or the workspace's (the wire <c>target.id</c>).</summary>
    public string Name => SessionName ?? WorkspaceName;

    /// <summary>The unit of the target's deletion item, <see cref="WorkUnitKeys.Deletion"/>.</summary>
    public string WorkUnitKey => WorkUnitKeys.Deletion(WorkspaceName, Kind, Name);

    public static DeletionTarget Session(string workspaceName, string sessionName)
    {
        ArgumentNullException.ThrowIfNull(workspaceName);
        ArgumentNullException.ThrowIfNull(sessionName);
        return new DeletionTarget(DeletionTargetKind.Session, workspaceName, sessionName);
    }

    public static DeletionTarget Workspace(string workspaceName)
    {
        ArgumentNullException.ThrowIfNull(workspaceName);
        return new DeletionTarget(DeletionTargetKind.Workspace, workspaceName, sessionName: null);
    }
}
