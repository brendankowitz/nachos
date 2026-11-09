using Nachos.Core.Memory;

namespace Nachos.Core.Context;

/// <summary>Whose representation and card a session context includes, and what the representation draws on.</summary>
public sealed record ContextRepresentation(string ObserverName, string ObservedName, RepresentationQuery Query);
