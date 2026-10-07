namespace Nachos.Abstractions.Filtering;

/// <summary>
/// Root of the list-filter syntax tree. Store list methods accept an optional node and the provider compiles it
/// to its native query. Concrete node types are added alongside the filter parser.
/// </summary>
public abstract record FilterNode;