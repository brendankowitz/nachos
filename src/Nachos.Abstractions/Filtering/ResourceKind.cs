namespace Nachos.Abstractions.Filtering;

/// <summary>The resource a filter applies to. It decides which wire field names exist and what type each has.</summary>
public enum ResourceKind
{
    Workspace,
    Peer,
    Session,
    Message,
}
