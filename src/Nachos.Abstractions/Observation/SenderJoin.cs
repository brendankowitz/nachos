using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions.Observation;

/// <summary>When a peer that joins a session by appending messages joins it. Both providers apply it.</summary>
public static class SenderJoin
{
    /// <summary>
    /// <paramref name="now"/>, or the earliest <c>CreatedAt</c> among <paramref name="sender"/>'s messages in the batch when
    /// that is earlier, so a backdated import falls inside its sender's own window (spec §9.4). A null <c>CreatedAt</c>
    /// counts as <paramref name="now"/>.
    /// </summary>
    public static DateTimeOffset JoinedAt(IEnumerable<NewMessage> messages, string sender, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages
            .Where(message => string.Equals(message.PeerName, sender, StringComparison.Ordinal))
            .Select(message => message.CreatedAt ?? now)
            .Append(now)
            .Min();
    }
}
