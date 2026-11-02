namespace Nachos.Abstractions.Domain;

/// <summary>
/// The kind of a work item. The values are the stored form and are reserved for their milestones now, so later
/// milestones need no schema change.
/// </summary>
public enum WorkTaskType
{
    /// <summary>Explicit-fact derivation for one observed sender in one session.</summary>
    Representation = 1,

    /// <summary>Session summaries (M3).</summary>
    Summary = 2,

    /// <summary>Dreams (M5).</summary>
    Dream = 3,

    /// <summary>Webhook dispatch (M6).</summary>
    Webhook = 4,

    /// <summary>Queued deletion (M3).</summary>
    Deletion = 5,

    /// <summary>Reconciliation (embedding sync, purges).</summary>
    Reconciler = 6,

    /// <summary>Scope backfill (M6).</summary>
    ScopeBackfill = 7,

    /// <summary>Scope removal (M6).</summary>
    ScopeRemoval = 8,
}
