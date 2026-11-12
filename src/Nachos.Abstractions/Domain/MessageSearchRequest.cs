using Nachos.Abstractions.Filtering;

namespace Nachos.Abstractions.Domain;

/// <summary>The candidate set of a message search: the scope narrowed by the filter.</summary>
/// <param name="Filter">A filter over <see cref="ResourceKind.Message"/> fields, or null for none.</param>
public sealed record MessageSearchRequest(string WorkspaceName, MessageSearchScope Scope, FilterNode? Filter);
