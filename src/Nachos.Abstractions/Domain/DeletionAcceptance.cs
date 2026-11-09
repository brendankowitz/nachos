namespace Nachos.Abstractions.Domain;

/// <summary>The outcome of an accepted delete (202).</summary>
/// <param name="JobId">The id of the target's deletion work item, the same for every repeat of the delete.</param>
/// <param name="AlreadyDeleting">True when the target was already being deleted, so this call changed nothing.</param>
public sealed record DeletionAcceptance(long JobId, bool AlreadyDeleting);
