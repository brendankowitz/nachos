namespace Nachos.Abstractions.Domain;

/// <summary>Which peers a peer listing returns.</summary>
public enum PeerKind
{
    Regular,
    Scope,
    All,
}