namespace Nachos.Abstractions.Domain;

/// <summary>Who last wrote a peer card.</summary>
public enum PeerCardAuthor
{
    /// <summary>A caller of the peer-card API.</summary>
    Api,

    /// <summary>A dream (M5).</summary>
    Dream,
}
