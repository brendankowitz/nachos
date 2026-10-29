namespace Nachos.Abstractions.Domain;

/// <summary>Whether an appended message enqueues derivation work (its resolved <c>reasoning.enabled</c>).</summary>
public enum DeriveIntent
{
    /// <summary>Enqueue nothing for the message.</summary>
    Skip,

    /// <summary>Enqueue a <see cref="WorkTaskType.Representation"/> item when the sender has at least one observer.</summary>
    Derive,
}
