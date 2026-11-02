using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Work;

namespace Nachos.Core.Deriver;

/// <summary>
/// A representation lease read as a batch: one workspace and session, the messages it was enqueued for, and what each
/// message's work item snapshotted when it was appended (observers and the message's own reasoning settings).
/// </summary>
internal sealed class WorkBatch
{
    private readonly IReadOnlyDictionary<string, RepresentationWork> _work;

    private WorkBatch(
        string workspaceName, string sessionName, IReadOnlyList<string> messageIds, IReadOnlyDictionary<string, RepresentationWork> work)
    {
        WorkspaceName = workspaceName;
        SessionName = sessionName;
        MessageIds = messageIds;
        _work = work;
    }

    public string WorkspaceName { get; }

    public string SessionName { get; }

    /// <summary>The batch's message ids in item order; each appears once.</summary>
    public IReadOnlyList<string> MessageIds { get; }

    /// <summary>The snapshot taken for <paramref name="messageId"/>, which must be a message of the batch.</summary>
    public RepresentationWork WorkOf(string messageId) => _work[messageId];

    /// <summary>Changes that only complete the batch's items.</summary>
    public DerivationChanges Nothing() => new(WorkspaceName, [], [], []);

    /// <exception cref="ArgumentException">The lease has no items or holds work of another task type.</exception>
    /// <exception cref="InvalidOperationException">
    /// An item lacks its workspace, session or message, repeats a message, or has an unreadable payload.
    /// </exception>
    public static WorkBatch From(WorkLease lease)
    {
        if (lease.Items.Count == 0)
        {
            throw new ArgumentException($"Lease on '{lease.WorkUnitKey}' has no work items to derive from.", nameof(lease));
        }

        if (lease.Items.Any(item => item.TaskType != WorkTaskType.Representation))
        {
            throw new ArgumentException($"Lease on '{lease.WorkUnitKey}' holds work that is not {WorkTaskType.Representation}.", nameof(lease));
        }

        var first = lease.Items[0];
        var workspaceName = first.WorkspaceName ?? throw Malformed(first, "has no workspace");
        var sessionName = first.SessionName ?? throw Malformed(first, "has no session");
        var work = new Dictionary<string, RepresentationWork>(StringComparer.Ordinal);
        var messageIds = new List<string>();
        foreach (var item in lease.Items)
        {
            if (!string.Equals(item.WorkspaceName, workspaceName, StringComparison.Ordinal)
                || !string.Equals(item.SessionName, sessionName, StringComparison.Ordinal))
            {
                throw Malformed(item, "belongs to another workspace or session than the rest of its unit");
            }

            var messageId = item.MessageId ?? throw Malformed(item, "has no message id");
            RepresentationWork snapshot;
            try
            {
                snapshot = RepresentationPayload.Read(item.Payload);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"Representation work item {item.Id} of unit '{item.WorkUnitKey}' has an unreadable payload: {ex.Message}", ex);
            }

            if (!work.TryAdd(messageId, snapshot))
            {
                throw Malformed(item, $"repeats message '{messageId}', which another item of the unit already covers");
            }

            messageIds.Add(messageId);
        }

        return new WorkBatch(workspaceName, sessionName, messageIds, work);
    }

    private static InvalidOperationException Malformed(WorkItemRecord item, string problem) =>
        new($"Representation work item {item.Id} of unit '{item.WorkUnitKey}' {problem}.");
}
