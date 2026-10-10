using System.Text;
using Microsoft.Extensions.Logging;

namespace Nachos.Client.Tests;

/// <summary>
/// The system clock, except that every timer due within 10 s fires at once, so backoff and short <c>Retry-After</c>
/// waits never sleep for real. Longer timers (the 30 s attempt timeout) keep their real due time.
/// </summary>
internal sealed class ZeroDelayTimeProvider : TimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        dueTime == Timeout.InfiniteTimeSpan || dueTime > TimeSpan.FromSeconds(10)
            ? System.CreateTimer(callback, state, dueTime, period)
            : System.CreateTimer(callback, state, TimeSpan.Zero, period);
}

/// <summary>
/// Captures every log message, scope, structured value and exception text, at every level. A structured provider
/// (OpenTelemetry, Serilog, Application Insights) exports the state's key/value pairs as they are, not the formatted
/// message, so the capture expands them the same way: a value that is a collection (the factory's header log values
/// are <c>string[]</c>) is written element by element, never as its type name.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<(string Category, bool Structured, string Text)> _entries = [];

    /// <summary>Everything captured: formatted messages, exception text, scopes and the expanded structured state.</summary>
    public string Text => TextOf(_ => true, structured: true);

    /// <summary>The formatted messages, exception text and scopes only, without the structured state.</summary>
    public string FormattedText => TextOf(_ => true, structured: false);

    /// <summary>The categories that received at least one entry.</summary>
    public IReadOnlyCollection<string> Categories
    {
        get
        {
            lock (_entries)
            {
                return _entries.Select(e => e.Category).Distinct(StringComparer.Ordinal).ToArray();
            }
        }
    }

    /// <summary>Everything captured for the categories whose name ends with <paramref name="categorySuffix"/>.</summary>
    public string TextOf(string categorySuffix) => TextOf(c => c.EndsWith(categorySuffix, StringComparison.Ordinal), structured: true);

    /// <summary>The formatted text (no structured state) of the categories whose name ends with <paramref name="categorySuffix"/>.</summary>
    public string FormattedTextOf(string categorySuffix) => TextOf(c => c.EndsWith(categorySuffix, StringComparison.Ordinal), structured: false);

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private string TextOf(Func<string, bool> category, bool structured)
    {
        var text = new StringBuilder();
        lock (_entries)
        {
            foreach (var (name, isStructured, line) in _entries)
            {
                if (category(name) && (structured || !isStructured))
                {
                    text.AppendLine(line);
                }
            }
        }

        return text.ToString();
    }

    private void Append(string category, bool structured, string line)
    {
        lock (_entries)
        {
            _entries.Add((category, structured, line));
        }
    }

    /// <summary>
    /// <paramref name="value"/> as a structured provider would export it: a string as it is, a key/value pair as
    /// <c>key=value</c>, a collection as its elements in brackets, recursively; anything else by <c>ToString</c>.
    /// </summary>
    private static string Expand(object? value) => value switch
    {
        null => "(null)",
        string text => text,
        KeyValuePair<string, object?> pair => $"{pair.Key}={Expand(pair.Value)}",
        System.Collections.IEnumerable items => "[" + string.Join(", ", items.Cast<object?>().Select(Expand)) + "]",
        _ => value.ToString() ?? "(null)",
    };

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            owner.Append(category, structured: false, $"{category} scope: {state}");
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            owner.Append(category, structured: false, $"{category} {logLevel}: {formatter(state, exception)} {exception}");
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var pair in values)
                {
                    owner.Append(category, structured: true, $"  {Expand(pair)}");
                }
            }
        }
    }
}
