namespace Nachos.Abstractions.Domain;

/// <summary>Lifecycle of a workspace or session.</summary>
public enum LifecycleState
{
    /// <summary>The resource is live (<c>is_active = true</c> on the wire).</summary>
    Active,

    /// <summary>The resource exists but is not live (<c>is_active = false</c> on the wire).</summary>
    Inactive,

    /// <summary>A queued delete is in progress; the resource is being removed.</summary>
    Deleting,
}