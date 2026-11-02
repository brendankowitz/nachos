namespace Nachos.Abstractions.Observation;

/// <summary>
/// Decides which peers form memories of a message. Both providers apply it once, when the message is appended, and the
/// result is stored with the work; nothing re-resolves it later.
/// </summary>
/// <remarks>
/// Defaults follow Honcho's public documentation: <c>observe_me</c> is true and <c>observe_others</c> false unless set.
/// </remarks>
public static class ObserverRule
{
    /// <summary>
    /// The observers of a message <paramref name="sender"/> sent at <paramref name="at"/>, ordinal-sorted and distinct.
    /// Nobody observes a sender whose peer-level or session-level <c>observe_me</c> is false. Otherwise the sender
    /// observes itself, and so does every other member that is active at <paramref name="at"/> and whose session-level
    /// <c>observe_others</c> is true.
    /// </summary>
    /// <param name="sender">The sender's settings.</param>
    /// <param name="members">The session's memberships, current and former; the sender's own is ignored.</param>
    /// <param name="at">The message's <c>CreatedAt</c>.</param>
    public static IReadOnlyList<string> Resolve(
        PeerObservation sender, IReadOnlyList<MemberObservation> members, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(members);

        if (sender.PeerObserveMe == false || sender.SessionObserveMe == false)
        {
            return [];
        }

        var observers = members
            .Where(member => member.ObserveOthers == true
                && member.IsActiveAt(at)
                && !string.Equals(member.PeerName, sender.PeerName, StringComparison.Ordinal))
            .Select(member => member.PeerName)
            .Append(sender.PeerName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return [.. observers];
    }
}
