namespace Nachos.Abstractions.Observation;

/// <summary>
/// One session membership as the observer rule sees it: the member's session-level <c>observe_others</c> (null means
/// "not set", the default, false) and the interval it is active, <c>JoinedAt &lt;= t &lt; LeftAt</c>.
/// </summary>
public sealed record MemberObservation(
    string PeerName, bool? ObserveOthers, DateTimeOffset JoinedAt, DateTimeOffset? LeftAt)
{
    public bool IsActiveAt(DateTimeOffset at) => JoinedAt <= at && (LeftAt is null || at < LeftAt);
}
