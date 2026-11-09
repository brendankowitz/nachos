using System.Diagnostics.CodeAnalysis;

namespace Nachos.Abstractions.Domain;

/// <summary>Which of a workspace's messages a search considers: one of the nested cases, and nothing else.</summary>
[SuppressMessage("Design", "CA1034", Justification = "The nested records are the closed set of cases of this union.")]
public abstract record MessageSearchScope
{
    private MessageSearchScope()
    {
    }

    /// <summary>Every session of the workspace.</summary>
    public sealed record Workspace : MessageSearchScope;

    /// <summary>One session.</summary>
    public sealed record Session(string SessionName) : MessageSearchScope;

    /// <summary>
    /// What one peer could have seen: a message is in scope when the peer's membership of the message's session has
    /// <c>JoinedAt &lt;= CreatedAt &lt; (LeftAt ?? forever)</c>.
    /// </summary>
    public sealed record PeerPerspective(string PeerName) : MessageSearchScope;
}
