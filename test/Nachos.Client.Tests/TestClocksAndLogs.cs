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
    private readonly StringBuilder _text = new();

    public string Text
    {
        get
        {
            lock (_text)
            {
                return _text.ToString();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private void Append(string line)
    {
        lock (_text)
        {
            _text.AppendLine(line);
        }
    }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            owner.Append($"{category} scope: {state}");
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            owner.Append($"{category} {logLevel}: {formatter(state, exception)} {exception}");
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var (key, value) in values)
                {
                    owner.Append($"  {key}={value}");
                }
            }
        }
    }
}
