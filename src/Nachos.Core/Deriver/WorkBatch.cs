using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Work;

namespace Nachos.Core.Deriver;

/// <summary>
/// A representation lease read as a batch: one workspace and session, the messages it was enqueued for, and the
/// observers each message had when it was appended.
/// </summary>
internal sealed class WorkBatch
{
    private readonly IReadOnlyDictionary<string, HashSet<string>> _observersByMessage;

    private WorkBatch(
        string workspaceName,
        string sessionName,
        IReadOnlyList<string> messageIds,
        IReadOnlyDictionary<string, HashSet<string>> observersByMessage)
    {
        WorkspaceName = workspaceName;
        SessionName = sessionName;
        MessageIds = messageIds;
        _observersByMessage = observersByMessage;
        Observers = [.. observersByMessage.Values.SelectMany(observers => observers).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    public string WorkspaceName { get; }

    public string SessionName { get; }

    /// <summary>The batch's message ids in item order.</summary>
    public IReadOnlyList<string> MessageIds { get; }

    /// <summary>Every observer of any message of the batch, ordinal-sorted.</summary>
    public IReadOnlyList<string> Observers { get; }

    /// <summary>The observers snapshotted for <paramref name="messageId"/>, which must be a message of the batch.</summary>
    public IReadOnlySet<string> ObserversOf(string messageId) => _observersByMessage[messageId];

    /// <summary>Changes that only complete the batch's items.</summary>
    public DerivationChanges Nothing() => new(WorkspaceName, [], [], []);

    /// <exception cref="ArgumentException">The lease has no items or holds work of another task type.</exception>
    /// <exception cref="InvalidOperationException">An item lacks its workspace, session, message or observers.</exception>
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
        var observersByMessage = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var messageIds = new List<string>();
        foreach (var item in lease.Items)
        {
            if (!string.Equals(item.WorkspaceName, workspaceName, StringComparison.Ordinal)
                || !string.Equals(item.SessionName, sessionName, StringComparison.Ordinal))
            {
                throw Malformed(item, "belongs to another workspace or session than the rest of its unit");
            }

            var messageId = item.MessageId ?? throw Malformed(item, "has no message id");
            IReadOnlyList<string> observers;
            try
            {
                observers = RepresentationPayload.ReadObservers(item.Payload);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"Representation work item {item.Id} of unit '{item.WorkUnitKey}' has an unreadable payload: {ex.Message}", ex);
            }

            if (observersByMessage.TryAdd(messageId, new HashSet<string>(observers, StringComparer.Ordinal)))
            {
                messageIds.Add(messageId);
            }
            else
            {
                observersByMessage[messageId].UnionWith(observers);
            }
        }

        return new WorkBatch(workspaceName, sessionName, messageIds, observersByMessage);
    }

    private static InvalidOperationException Malformed(WorkItemRecord item, string problem) =>
        new($"Representation work item {item.Id} of unit '{item.WorkUnitKey}' {problem}.");
}
