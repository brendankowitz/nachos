using Nachos.Core.Memory;

namespace Nachos.Core.Context;

/// <summary>Whose representation and card a session context includes, and what the representation draws on.</summary>
/// <param name="ObserverName">The peer whose memory is read.</param>
/// <param name="ObservedName">The peer it is about.</param>
/// <param name="Query">What the representation draws on.</param>
/// <param name="Sessions">
/// The request's own narrowing (<c>limit_to_session</c>, <c>sessions</c>): the sessions the representation may draw on,
/// or null for no narrowing. Any narrowing hides the card. It applies to the representation and card only, never to the
/// session's own messages and summary.
/// </param>
public sealed record ContextRepresentation(
    string ObserverName, string ObservedName, RepresentationQuery Query, IReadOnlyCollection<string>? Sessions = null);
