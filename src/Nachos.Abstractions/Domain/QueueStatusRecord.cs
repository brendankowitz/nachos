namespace Nachos.Abstractions.Domain;

/// <summary>
/// Queue counts for the selected work, overall and per session (keyed by session name) for session-scoped work. A session
/// with no selected work has no entry.
/// </summary>
public sealed record QueueStatusRecord(QueueCounts Totals, IReadOnlyDictionary<string, QueueCounts> Sessions);
