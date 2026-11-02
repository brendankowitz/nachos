namespace Nachos.Abstractions.Domain;

/// <summary>
/// An appended message's derivation decision, made at append time, and the message's own <c>reasoning</c> settings,
/// which the work item snapshots so they still apply at derive time.
/// </summary>
/// <param name="ShouldDerive">
/// The message's resolved <c>reasoning.enabled</c> (message over session over workspace over global): whether it enqueues a
/// <see cref="WorkTaskType.Representation"/> item, when the sender has at least one observer.
/// </param>
/// <param name="ReasoningEnabled">
/// The message's own <c>reasoning.enabled</c>, or null when the message does not set it. Message configuration is not
/// stored with the message, so this snapshot is how it keeps overriding a session or workspace changed after the append.
/// </param>
/// <param name="CustomInstructions">The message's own <c>reasoning.custom_instructions</c>, or null.</param>
public sealed record DeriveIntent(bool ShouldDerive, bool? ReasoningEnabled = null, string? CustomInstructions = null)
{
    /// <summary>Enqueue nothing for the message.</summary>
    public static DeriveIntent Skip { get; } = new(false);

    /// <summary>Enqueue work for the message, which sets no reasoning of its own.</summary>
    public static DeriveIntent Derive { get; } = new(true);
}
