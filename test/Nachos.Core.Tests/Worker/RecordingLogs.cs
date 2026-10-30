using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Nachos.Core.Tests.Worker;

/// <summary>
/// Every entry at <see cref="LogLevel.Information"/> or above logged through a <see cref="RecordingLogger{T}"/> sharing
/// this instance, in order.
/// </summary>
internal sealed class RecordingLogs
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public IReadOnlyList<string> At(LogLevel level) => [.. Entries.Where(e => e.Level == level).Select(e => e.Message)];
}

internal sealed class RecordingLogger<T>(RecordingLogs logs) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            logs.Entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
