namespace Nachos.Abstractions.Domain;

/// <summary>Who last wrote a peer card. The values are the stored form.</summary>
public enum PeerCardAuthor
{
    /// <summary>A caller of the peer-card API.</summary>
    Api = 0,

    /// <summary>A dream (M5).</summary>
    Dream = 1,
}
