using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Work;
using Nachos.Core.Configuration;
using Nachos.Core.Llm;
using Nachos.Core.Tokens;
using Nachos.Core.Worker;

namespace Nachos.Core.Summarizer;

/// <summary>
/// The <see cref="WorkTaskType.Summary"/> handler: brings a session's short and long summaries up to the <c>Seq</c> at which
/// each came due (spec §11.3).
/// </summary>
/// <remarks>
/// <para>
/// One lease is one session and can hold a short item, a long item or both. Each kind is cumulative: the prior summary of
/// that kind plus the messages after its coverage point, up to the highest <c>through_seq</c> among the lease's items of
/// the kind, are summarized in one call. When both kinds are due the calls run concurrently. Nothing is written until both
/// have answered: all results go to the store in one <see cref="ISummaryStore.SaveAsync"/>, which also completes the
/// lease, so a failure of either call leaves no trace and the unit is retried whole.
/// </para>
/// <para>
/// The request is bounded to <see cref="SummaryOptions.MaxInputTokens"/> by dropping the oldest new messages; the summary
/// still records that it covers through the newest. When summaries are disabled by the time the unit is handled, or the
/// session is gone, the lease completes without a model call.
/// </para>
/// <para>Scoped, because it resolves configuration through the scoped <see cref="IConfigurationResolver"/>.</para>
/// </remarks>
public sealed partial class SummarizerService : IWorkHandler
{
    public const string AgentName = "summarizer";

    /// <summary>How many sequence numbers one read covers while collecting the newest messages that fit the budget.</summary>
    private const int ReadWindow = 200;

    private static readonly PromptTemplate ShortPrompt =
        PromptTemplate.LoadEmbedded(typeof(SummarizerService).Assembly, "Nachos.Core.Summarizer.Prompts.summary-short.v1.md");

    private static readonly PromptTemplate LongPrompt =
        PromptTemplate.LoadEmbedded(typeof(SummarizerService).Assembly, "Nachos.Core.Summarizer.Prompts.summary-long.v1.md");

    // The input is model text, not HTML: keep non-ASCII readable. Quotes and backslashes are still escaped, so message
    // content cannot break out of its JSON string.
    private static readonly JsonSerializerOptions InputJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IMemoryStore _store;
    private readonly NachosChatClients _chatClients;
    private readonly IConfigurationResolver _configurationResolver;
    private readonly ITokenCounter _tokenCounter;
    private readonly SummaryOptions _options;
    private readonly ILogger<SummarizerService> _logger;

    public SummarizerService(
        IMemoryStore store,
        NachosChatClients chatClients,
        IConfigurationResolver configurationResolver,
        ITokenCounter tokenCounter,
        IOptions<NachosOptions> options,
        ILogger<SummarizerService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(chatClients);
        ArgumentNullException.ThrowIfNull(configurationResolver);
        ArgumentNullException.ThrowIfNull(tokenCounter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _chatClients = chatClients;
        _configurationResolver = configurationResolver;
        _tokenCounter = tokenCounter;
        _options = options.Value.Summary;
        _logger = logger;
    }

    public WorkTaskType TaskType => WorkTaskType.Summary;

    public WorkBatchPolicy BatchPolicy => WorkBatchPolicy.Immediate;

    /// <exception cref="ArgumentException">The lease has no items or holds work that is not a summary.</exception>
    /// <exception cref="InvalidOperationException">A work item is malformed, or the model answered with no text.</exception>
    /// <exception cref="LeaseLostException">The lease was lost; nothing was written.</exception>
    public async Task HandleAsync(WorkLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var (workspaceName, sessionName, due) = ReadDue(lease);

        IReadOnlyList<NewSummary> summaries;
        try
        {
            summaries = await SummarizeAsync(workspaceName, sessionName, due, cancellationToken).ConfigureAwait(false);
        }
        catch (NotFoundException ex)
        {
            LogSourceGone(lease.WorkUnitKey, ex.Message);
            summaries = [];
        }

        await _store.Summaries.SaveAsync(lease, workspaceName, sessionName, summaries, cancellationToken).ConfigureAwait(false);
        LogCommitted(lease.WorkUnitKey, summaries.Count);
    }

    /// <summary>The highest <c>through_seq</c> per kind among the lease's items.</summary>
    private static (string WorkspaceName, string SessionName, Dictionary<SummaryKind, long> Due) ReadDue(WorkLease lease)
    {
        if (lease.Items.Count == 0)
        {
            throw new ArgumentException($"Lease on '{lease.WorkUnitKey}' has no work items to summarize.", nameof(lease));
        }

        var first = lease.Items[0];
        var workspaceName = first.WorkspaceName ?? throw Malformed(first, "has no workspace");
        var sessionName = first.SessionName ?? throw Malformed(first, "has no session");
        var due = new Dictionary<SummaryKind, long>();
        foreach (var item in lease.Items)
        {
            if (item.TaskType != WorkTaskType.Summary)
            {
                throw new ArgumentException($"Lease on '{lease.WorkUnitKey}' holds work that is not {WorkTaskType.Summary}.", nameof(lease));
            }

            if (!string.Equals(item.WorkspaceName, workspaceName, StringComparison.Ordinal)
                || !string.Equals(item.SessionName, sessionName, StringComparison.Ordinal))
            {
                throw Malformed(item, "belongs to another workspace or session than the rest of its unit");
            }

            SummaryWork work;
            try
            {
                work = SummaryPayload.Read(item.Payload);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"Summary work item {item.Id} of unit '{item.WorkUnitKey}' has an unreadable payload: {ex.Message}", ex);
            }

            due[work.Kind] = Math.Max(due.GetValueOrDefault(work.Kind), work.ThroughSeq);
        }

        return (workspaceName, sessionName, due);
    }

    private static InvalidOperationException Malformed(WorkItemRecord item, string problem) =>
        new($"Summary work item {item.Id} of unit '{item.WorkUnitKey}' {problem}.");

    private async Task<IReadOnlyList<NewSummary>> SummarizeAsync(
        string workspaceName, string sessionName, Dictionary<SummaryKind, long> due, CancellationToken ct)
    {
        var workspace = await _store.Workspaces.GetAsync(workspaceName, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Workspace '{workspaceName}' not found.");
        var session = await _store.Sessions.GetAsync(workspaceName, sessionName, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Session '{sessionName}' not found in workspace '{workspaceName}'.");
        var configuration = _configurationResolver.Resolve(workspace.Configuration, session.Configuration);
        if (configuration.Summary.Enabled.Value == false)
        {
            LogDisabled(workspaceName, sessionName);
            return [];
        }

        var instructions = RenderCustomInstructions(configuration);
        var results = await Task.WhenAll(due.Select(entry => SummarizeKindAsync(
            workspaceName, sessionName, entry.Key, entry.Value, instructions, configuration.Summary, ct))).ConfigureAwait(false);
        return [.. results.OfType<NewSummary>()];
    }

    /// <returns>The new summary, or null when the kind has nothing new to cover.</returns>
    private async Task<NewSummary?> SummarizeKindAsync(
        string workspaceName,
        string sessionName,
        SummaryKind kind,
        long throughSeq,
        string customInstructions,
        ResolvedSummary configuration,
        CancellationToken ct)
    {
        var prior = await _store.Summaries.GetAsync(workspaceName, sessionName, kind, ct).ConfigureAwait(false);
        var fromSeq = (prior?.CoversThroughSeq ?? 0) + 1;
        if (fromSeq > throughSeq)
        {
            return null;
        }

        var system = (kind == SummaryKind.Short ? ShortPrompt : LongPrompt)
            .Render(new Dictionary<string, string> { ["custom_instructions"] = customInstructions });
        var messages = await NewestFittingAsync(workspaceName, sessionName, kind, prior, fromSeq, throughSeq, system, ct)
            .ConfigureAwait(false);
        if (messages.Count == 0)
        {
            return null;
        }

        var maxOutputTokens = kind == SummaryKind.Short ? configuration.MaxTokensShort.Value : configuration.MaxTokensLong.Value;
        var content = await CompleteAsync(workspaceName, sessionName, kind, system, Input(kind, prior, messages), maxOutputTokens, ct)
            .ConfigureAwait(false);
        var last = messages[^1];
        return new NewSummary(kind, content, last.PublicId, last.Seq, _tokenCounter.Count(content));
    }

    /// <summary>
    /// The non-blank messages in <c>[fromSeq, throughSeq]</c> that fit <see cref="SummaryOptions.MaxInputTokens"/> together
    /// with the prompt and the prior summary, newest first until one does not fit, in <c>Seq</c> order. The newest message is
    /// always included, because the summary's coverage ends at it.
    /// </summary>
    private async Task<IReadOnlyList<MessageRecord>> NewestFittingAsync(
        string workspaceName,
        string sessionName,
        SummaryKind kind,
        SummaryRecord? prior,
        long fromSeq,
        long throughSeq,
        string system,
        CancellationToken ct)
    {
        var remaining = _options.MaxInputTokens - _tokenCounter.Count(system) - _tokenCounter.Count(Input(kind, prior, []));
        var kept = new List<MessageRecord>();
        for (var high = throughSeq; high >= fromSeq; high -= ReadWindow)
        {
            var window = await _store.Messages.ListRangeAsync(
                workspaceName, sessionName, Math.Max(fromSeq, high - ReadWindow + 1), high, ct).ConfigureAwait(false);
            foreach (var message in window.Where(m => !string.IsNullOrWhiteSpace(m.Content)).OrderByDescending(m => m.Seq))
            {
                var cost = _tokenCounter.Count(Entry(message).ToJsonString(InputJson));
                if (cost > remaining)
                {
                    if (kept.Count > 0)
                    {
                        LogInputBounded(workspaceName, sessionName, kind, kept.Count, truncated: false);
                        kept.Reverse();
                        return kept;
                    }

                    // The newest message anchors the summary's coverage, so it stays, cut down to what is left of the budget.
                    kept.Add(message with { Content = Truncate(message, Math.Max(0, remaining)) });
                    LogInputBounded(workspaceName, sessionName, kind, 1, truncated: true);
                    return kept;
                }

                remaining -= cost;
                kept.Add(message);
            }
        }

        kept.Reverse();
        return kept;
    }

    /// <summary>The longest prefix of the message's content whose entry costs at most <paramref name="budget"/> tokens.</summary>
    private string Truncate(MessageRecord message, int budget)
    {
        var low = 0;
        var high = message.Content.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (_tokenCounter.Count(Entry(message with { Content = message.Content[..middle] }).ToJsonString(InputJson)) <= budget)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low > 0 && char.IsHighSurrogate(message.Content[low - 1]) ? message.Content[..(low - 1)] : message.Content[..low];
    }

    private static JsonObject Entry(MessageRecord message) => new()
    {
        ["speaker"] = message.PeerName,
        ["sent_at"] = message.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
        ["content"] = message.Content,
    };

    private static string Input(SummaryKind kind, SummaryRecord? prior, IReadOnlyList<MessageRecord> messages) =>
        new JsonObject
        {
            ["summary_kind"] = kind == SummaryKind.Short ? "short" : "long",
            ["prior_summary"] = prior?.Content,
            ["messages"] = new JsonArray([.. messages.Select(message => (JsonNode)Entry(message))]),
        }.ToJsonString(InputJson);

    private async Task<string> CompleteAsync(
        string workspaceName, string sessionName, SummaryKind kind, string system, string input, int maxOutputTokens, CancellationToken ct)
    {
        var options = AgentCallTags.CreateOptions(AgentName, workspaceName);
        options.MaxOutputTokens = maxOutputTokens;
        var response = await _chatClients.ForAgent(AgentName).GetResponseAsync(
            [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, input)],
            options,
            ct).ConfigureAwait(false);
        var text = response.Text.Trim();
        return text.Length > 0
            ? text
            : throw new InvalidOperationException(
                $"The summarizer model's {kind} summary of session '{sessionName}' in workspace '{workspaceName}' is empty " +
                $"(finish reason: {response.FinishReason?.Value ?? "none"}); the unit will be retried.");
    }

    /// <summary>The resolved general and summary instructions, for the system prompt.</summary>
    private static string RenderCustomInstructions(ResolvedConfiguration configuration)
    {
        var present = new[] { configuration.CustomInstructions.Value, configuration.Summary.CustomInstructions.Value }
            .OfType<string>()
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();
        return present.Count == 0
            ? string.Empty
            : $"\n## Additional instructions\n\n{string.Join("\n\n", present)}\n";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Summary unit {WorkUnitKey} committed {Summaries} summaries.")]
    private partial void LogCommitted(string workUnitKey, int summaries);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Kind} summary input of session {SessionName} in workspace {WorkspaceName} exceeds Summary:MaxInputTokens; keeping the newest {Kept} message(s) (newest truncated: {Truncated}) and leaving the older ones out.")]
    private partial void LogInputBounded(string workspaceName, string sessionName, SummaryKind kind, int kept, bool truncated);

    [LoggerMessage(Level = LogLevel.Information, Message = "Summaries are disabled for session {SessionName} in workspace {WorkspaceName}; completing without a model call.")]
    private partial void LogDisabled(string workspaceName, string sessionName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Summary unit {WorkUnitKey} has nothing to summarize: {Reason}")]
    private partial void LogSourceGone(string workUnitKey, string reason);
}
