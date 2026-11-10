namespace Nachos.Core.Llm;

/// <summary>
/// Holds back every embedding while the configured model has not been checked against the database's stamp
/// (<see cref="EmbeddingSpaceCheck"/>), so a host whose store was unreachable at startup cannot write vectors of an unchecked
/// model in the meantime. Open unless the check closed it; callers wait rather than fail, because a failed embedding would
/// count against the row being embedded.
/// </summary>
public sealed class EmbeddingSpaceGate
{
    private volatile TaskCompletionSource _opened = Opened();

    /// <summary>True while embeddings may run.</summary>
    public bool IsOpen => _opened.Task.IsCompleted;

    /// <summary>Waits until the gate is open.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled first.</exception>
    public Task WaitAsync(CancellationToken cancellationToken) => _opened.Task.WaitAsync(cancellationToken);

    /// <summary>Holds back embeddings from now until <see cref="Open"/>.</summary>
    public void Close()
    {
        if (IsOpen)
        {
            _opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Releases every waiting and future embedding.</summary>
    public void Open() => _opened.TrySetResult();

    private static TaskCompletionSource Opened()
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        opened.SetResult();
        return opened;
    }
}
