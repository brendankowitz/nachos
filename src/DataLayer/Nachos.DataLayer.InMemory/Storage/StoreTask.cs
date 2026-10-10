using System.Diagnostics.CodeAnalysis;

namespace Nachos.DataLayer.InMemory.Storage;

/// <summary>
/// Runs a synchronous store operation behind the async store interfaces. Failures are returned as faulted tasks, not
/// thrown, so callers observe them on <c>await</c> exactly as with an I/O-bound provider, and the original exception
/// propagates unwrapped.
/// </summary>
internal static class StoreTask
{
    [SuppressMessage("Design", "CA1031", Justification = "Every exception is handed to the caller through the task.")]
    public static Task<T> Run<T>(Func<T> operation, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(ct);
        }

        try
        {
            return Task.FromResult(operation());
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    public static Task Run(Action operation, CancellationToken ct) =>
        Run(
            () =>
            {
                operation();
                return true;
            },
            ct);
}
