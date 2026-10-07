namespace Nachos.Abstractions.Domain;

/// <summary>Lifecycle of a workspace or session.</summary>
public enum LifecycleState
{
    Active,
    Inactive,
    Deleting,
}