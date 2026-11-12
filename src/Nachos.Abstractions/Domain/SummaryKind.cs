using System.Diagnostics.CodeAnalysis;

namespace Nachos.Abstractions.Domain;

/// <summary>The kind of a session summary. The values are the stored form.</summary>
[SuppressMessage("Naming", "CA1720", Justification = "Short and long are the wire's summary kinds (short_summary, long_summary).")]
public enum SummaryKind
{
    /// <summary>Concise factual coverage, due every <see cref="SummaryIntent.ShortEvery"/> messages.</summary>
    Short = 1,

    /// <summary>Themes, context and apparent preferences, due every <see cref="SummaryIntent.LongEvery"/> messages.</summary>
    Long = 2,
}
