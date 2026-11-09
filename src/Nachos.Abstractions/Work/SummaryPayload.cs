using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions.Work;

/// <summary>
/// The payload of a <see cref="WorkTaskType.Summary"/> item: <c>{"kind": "short"|"long", "through_seq": n}</c>, the
/// kind that came due and the <c>Seq</c> it came due at.
/// </summary>
public static class SummaryPayload
{
    private const string KindKey = "kind";
    private const string ThroughSeqKey = "through_seq";

    public static JsonObject Create(SummaryKind kind, long throughSeq)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(throughSeq, 1);
        return new JsonObject { [KindKey] = Name(kind), [ThroughSeqKey] = throughSeq };
    }

    /// <exception cref="FormatException">The payload has no valid <c>kind</c> or positive integer <c>through_seq</c>.</exception>
    public static SummaryWork Read(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var kind = payload[KindKey] is JsonValue kindValue && kindValue.GetValueKind() == JsonValueKind.String
            ? kindValue.GetValue<string>() switch
            {
                "short" => SummaryKind.Short,
                "long" => SummaryKind.Long,
                var other => throw new FormatException($"A summary work item's '{KindKey}' must be 'short' or 'long', not '{other}'."),
            }
            : throw new FormatException($"A summary work item's payload must have a string '{KindKey}'.");
        return payload[ThroughSeqKey] is JsonValue seqValue
            && seqValue.GetValueKind() == JsonValueKind.Number
            && seqValue.TryGetValue(out long throughSeq)
            && throughSeq >= 1
            ? new SummaryWork(kind, throughSeq)
            : throw new FormatException($"A summary work item's '{ThroughSeqKey}' must be a positive integer.");
    }

    private static string Name(SummaryKind kind) => kind switch
    {
        SummaryKind.Short => "short",
        SummaryKind.Long => "long",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown summary kind."),
    };
}
