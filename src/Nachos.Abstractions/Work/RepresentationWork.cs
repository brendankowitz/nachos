namespace Nachos.Abstractions.Work;

/// <summary>A read <see cref="RepresentationPayload"/>.</summary>
/// <param name="Observers">The observers snapshotted when the message was appended.</param>
/// <param name="ReasoningEnabled">The message's own <c>reasoning.enabled</c>, or null when it set none.</param>
/// <param name="CustomInstructions">The message's own <c>reasoning.custom_instructions</c>, or null.</param>
public sealed record RepresentationWork(IReadOnlyList<string> Observers, bool? ReasoningEnabled, string? CustomInstructions);
