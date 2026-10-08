using Nachos.Abstractions.Domain;

namespace Nachos.DataLayer.InMemory.Storage;

/// <summary>
/// Enforces that an <see cref="IdempotencyWrite.SerializeResponse"/> callback is a pure function of the records it is
/// given: while it runs, every entry point of the same store rejects calls from the execution context running it.
/// </summary>
/// <remarks>
/// <para>
/// The marker is an <see cref="AsyncLocal{T}"/>, so it flows into work the callback starts (<c>Task.Run</c>, thread-pool
/// items). A callback that blocks on such work therefore gets a rejection instead of waiting forever for the workspace
/// gate its own thread holds. Other execution contexts, including concurrent appends, never see it.
/// </para>
/// <para>
/// A rejected call is recorded on the invocation, and the append fails after the callback returns even if the callback
/// swallowed the rejection. Work the callback started keeps the marker in its flowed context, so it is rejected even if
/// it runs after the callback returned.
/// </para>
/// <para>
/// Out of contract: a callback that does not flow its context (<see cref="ExecutionContext.SuppressFlow"/>, or
/// <see cref="ExecutionContext.Run"/> with a context captured outside) defeats the guard. Work on another thread then
/// waits for the workspace gate; a call on the callback's own thread re-enters the gate and is caught at commit by
/// <see cref="StagedAppend.IsCurrent"/> if it changed anything the append staged.
/// </para>
/// </remarks>
internal sealed class SerializeResponseGuard
{
    /// <summary>Names the rule; deliberately carries no data.</summary>
    public const string ReentryMessage = "The SerializeResponse callback must not call the store.";

    private readonly AsyncLocal<Invocation?> _current = new();

    /// <summary>
    /// Null when the calling execution context is not inside a callback of this store. Otherwise records the attempt
    /// and returns the exception the entry point must fail with, before it takes any lock or touches any state.
    /// </summary>
    public InvalidOperationException? Reject()
    {
        if (_current.Value is not { } invocation)
        {
            return null;
        }

        invocation.RecordReentry();
        return new InvalidOperationException(ReentryMessage);
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
            // The store completes synchronously, so this write lands in the caller's own context: it must be undone.
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
