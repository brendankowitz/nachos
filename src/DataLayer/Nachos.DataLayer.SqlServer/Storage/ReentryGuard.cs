using Nachos.Abstractions.Domain;

namespace Nachos.DataLayer.SqlServer.Storage;

/// <summary>
/// Enforces that an <see cref="IdempotencyWrite.SerializeResponse"/> callback is a pure function of the records it is
/// given: while it runs, every entry point of the same store rejects calls from the execution context running it.
/// </summary>
/// <remarks>
/// <para>
/// The callback runs inside the append's open transaction, after the rows are staged and before commit. A store call
/// from it would either read uncommitted state through another connection, or block on the locks its own transaction
/// holds. So the marker, an <see cref="AsyncLocal{T}"/>, flows into work the callback starts (<c>Task.Run</c>,
/// thread-pool items) and every entry point throws <see cref="InvalidOperationException"/> before it opens a connection.
/// Other execution contexts, including concurrent appends, never see it.
/// </para>
/// <para>
/// A rejected call is recorded on the invocation, so the append fails (and rolls back) after the callback returns even
/// if the callback swallowed the rejection. A callback that suppresses execution-context flow defeats the guard and is
/// out of contract.
/// </para>
/// </remarks>
internal sealed class ReentryGuard
{
    /// <summary>Names the rule; deliberately carries no data.</summary>
    public const string ReentryMessage = "The SerializeResponse callback must not call the store.";

    private readonly AsyncLocal<Invocation?> _current = new();

    /// <summary>Throws when the calling execution context is inside a callback of this store, recording the attempt.</summary>
    /// <exception cref="InvalidOperationException">The store was re-entered from a <c>SerializeResponse</c> callback.</exception>
    public void ThrowIfReentered()
    {
        if (_current.Value is { } invocation)
        {
            invocation.RecordReentry();
            throw new InvalidOperationException(ReentryMessage);
        }
    }

    /// <summary>
    /// Runs <paramref name="serialize"/> with the guard raised. An exception it throws propagates unchanged; if it
    /// returns after anything tried to re-enter the store, throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public string Invoke(Func<IReadOnlyList<MessageRecord>, string> serialize, IReadOnlyList<MessageRecord> records)
    {
        var invocation = new Invocation();
        _current.Value = invocation;
        string body;
        try
        {
            body = serialize(records);
        }
        finally
        {
            // The callback runs synchronously in the append's own context, so this write must be undone before it awaits.
            _current.Value = null;
        }

        return invocation.ReentryAttempted ? throw new InvalidOperationException(ReentryMessage) : body;
    }

    /// <summary>One callback run. Flowed contexts share the instance, so an attempt on any thread is seen here.</summary>
    private sealed class Invocation
    {
        private volatile bool _reentryAttempted;

        public bool ReentryAttempted => _reentryAttempted;

        public void RecordReentry() => _reentryAttempted = true;
    }
}
