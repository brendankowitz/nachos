namespace Nachos.Abstractions.Domain;

/// <summary>
/// Which peers a peer listing returns, keyed on <see cref="PeerRecord.IsInternal"/>. On the wire a missing kind
/// means <see cref="Regular"/>, and <c>"scope"</c> / <c>"all"</c> select the other two.
/// </summary>
public enum PeerKind
{
    /// <summary>User-created peers only (<c>IsInternal = false</c>).</summary>
    Regular,

    /// <summary>Internal (scope) peers only (<c>IsInternal = true</c>).</summary>
    Scope,

    /// <summary>Both regular and internal peers.</summary>
    All,
}