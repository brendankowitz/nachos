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

/// <summary>Captures every log message, scope, structured value and exception text, at every level.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<(string Category, string Text)> _entries = [];

    public string Text => TextOf(_ => true);

    /// <summary>The captured text of the categories whose name ends with <paramref name="categorySuffix"/>.</summary>
    public string TextOf(string categorySuffix) => TextOf(c => c.EndsWith(categorySuffix, StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private string TextOf(Func<string, bool> category)
    {
        var text = new StringBuilder();
        lock (_entries)
        {
            foreach (var (name, line) in _entries)
            {
                if (category(name))
                {
                    text.AppendLine(line);
                }
            }
        }

        return text.ToString();
    }

    private void Append(string category, string line)
    {
        lock (_entries)
        {
            _entries.Add((category, line));
        }
    }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            owner.Append(category, $"{category} scope: {state}");
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            owner.Append(category, $"{category} {logLevel}: {formatter(state, exception)} {exception}");
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var (key, value) in values)
                {
                    owner.Append(category, $"  {key}={value}");
                }
            }
        }
    }
}
