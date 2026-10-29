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

namespace Nachos.Core.Deriver;

/// <summary>
/// The <see cref="WorkTaskType.Representation"/> handler: turns one leased batch of a sender's messages into explicit
/// conclusions in every observer's collection (spec §11.2).
/// </summary>
/// <remarks>
/// <para>
/// Nothing is written until the end: every insert, corroboration and replacement of the batch, and the completion of its
/// items, go to the store in one <see cref="IConclusionStore.CommitDerivationAsync"/>. A failure anywhere before that,
/// including a lost lease, leaves no trace, so a retry recomputes the batch from scratch and cannot count a fact twice.
/// </para>
/// <para>
/// The observers are the snapshot each work item carries (<see cref="RepresentationPayload"/>), never the current
/// memberships. A fact reaches an observer only if that observer saw every message the fact is sourced from.
/// </para>
/// <para>Scoped, because it resolves configuration through the scoped <see cref="IConfigurationResolver"/>.</para>
/// </remarks>
public sealed partial class DeriverService : IWorkHandler
{
    public const string AgentName = "deriver";

    /// <summary>How many messages before the batch's first message are offered as context, budget permitting.</summary>
    private const int PrecedingContextMessages = 10;

    private const string PromptResource = "Nachos.Core.Deriver.Prompts.deriver.v1.md";

    private static readonly PromptTemplate Prompt = PromptTemplate.LoadEmbedded(typeof(DeriverService).Assembly, PromptResource);

    // The transcript is model input, not HTML, so non-ASCII text stays readable instead of becoming \u escapes.
    private static readonly JsonSerializerOptions TranscriptJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IMemoryStore _store;
    private readonly NachosChatClients _chatClients;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly IConfigurationResolver _configurationResolver;
    private readonly ITokenCounter _tokenCounter;
    private readonly ConclusionDeduplicator _deduplicator;
    private readonly DeriverOptions _options;
    private readonly ILogger<DeriverService> _logger;

    public DeriverService(
        IMemoryStore store,
        NachosChatClients chatClients,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        IConfigurationResolver configurationResolver,
        ITokenCounter tokenCounter,
        ConclusionDeduplicator deduplicator,
        IOptions<NachosOptions> options,
        ILogger<DeriverService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(chatClients);
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(configurationResolver);
        ArgumentNullException.ThrowIfNull(tokenCounter);
        ArgumentNullException.ThrowIfNull(deduplicator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _chatClients = chatClients;
        _embeddings = embeddings;
        _configurationResolver = configurationResolver;
        _tokenCounter = tokenCounter;
        _deduplicator = deduplicator;
        _options = options.Value.Deriver;
        _logger = logger;
        BatchPolicy = new WorkBatchPolicy(_options.EligibilityTokens, _options.MaxAge, _options.BatchTokenTarget);
    }

    public WorkTaskType TaskType => WorkTaskType.Representation;

    public WorkBatchPolicy BatchPolicy { get; }

    /// <exception cref="ArgumentException">The lease has no items or is not a representation unit.</exception>
    /// <exception cref="InvalidOperationException">
    /// A work item is malformed, the model's answer is not an <see cref="ExtractionResult"/>, or the embedding generator
    /// returned the wrong number or size of vectors.
    /// </exception>
    /// <exception cref="LeaseLostException">The lease was lost; nothing was written.</exception>
    public async Task HandleAsync(WorkLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var batch = WorkBatch.From(lease);

        var configuration = await ResolveConfigurationAsync(batch, cancellationToken).ConfigureAwait(false);
        if (configuration.Reasoning.Enabled.Value is false)
        {
            LogReasoningDisabled(lease.WorkUnitKey, lease.Items.Count);
            await CommitAsync(lease, batch.Nothing(), 0, cancellationToken).ConfigureAwait(false);
            return;
        }

        var messages = await LoadMessagesAsync(batch, cancellationToken).ConfigureAwait(false);
        if (messages.Count == 0)
        {
            await CommitAsync(lease, batch.Nothing(), 0, cancellationToken).ConfigureAwait(false);
            return;
        }

        var target = messages[0].PeerName;
        if (messages.Any(message => !string.Equals(message.PeerName, target, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Work unit '{lease.WorkUnitKey}' holds messages from more than one sender; a representation unit has one.");
        }

        var system = Prompt.Render(new Dictionary<string, string>
        {
            ["target_peer"] = target,
            ["custom_instructions"] = RenderCustomInstructions(configuration),
        });
        var transcript = await BuildTranscriptAsync(batch, messages, target, system, cancellationToken).ConfigureAwait(false);
        var extraction = await ExtractAsync(lease.WorkUnitKey, batch.WorkspaceName, system, transcript, cancellationToken)
            .ConfigureAwait(false);
        var candidates = Validate(lease.WorkUnitKey, extraction, messages);
        var facts = await EmbedAsync(lease.WorkUnitKey, candidates, cancellationToken).ConfigureAwait(false);
        var changes = await BuildChangesAsync(batch, target, facts, cancellationToken).ConfigureAwait(false);
        await CommitAsync(lease, changes, facts.Count, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResolvedConfiguration> ResolveConfigurationAsync(WorkBatch batch, CancellationToken ct)
    {
        var workspace = await _store.Workspaces.GetAsync(batch.WorkspaceName, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Workspace '{batch.WorkspaceName}' not found.");
        var session = await _store.Sessions.GetAsync(batch.WorkspaceName, batch.SessionName, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Session '{batch.SessionName}' not found in workspace '{batch.WorkspaceName}'.");
        // Message-level reasoning was applied at enqueue (it decided whether the item exists); it is not stored.
        return _configurationResolver.Resolve(workspace.Configuration, session.Configuration);
    }

    /// <summary>The batch's messages in <c>Seq</c> order. A message deleted since it was enqueued is left out.</summary>
    private async Task<List<MessageRecord>> LoadMessagesAsync(WorkBatch batch, CancellationToken ct)
    {
        var messages = new List<MessageRecord>();
        foreach (var messageId in batch.MessageIds)
        {
            if (await _store.Messages.GetAsync(batch.WorkspaceName, batch.SessionName, messageId, ct).ConfigureAwait(false)
                is { } message)
            {
                messages.Add(message);
            }
        }

        messages.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        return messages;
    }

    /// <summary>
    /// The user message: the batch's messages marked <c>target</c>, plus the messages around and before them as
    /// <c>context</c>, newest first while they fit <see cref="DeriverOptions.MaxInputTokens"/>. The batch is always
    /// included, even over budget, because it is the subject of the request; context stops at the first message that
    /// does not fit, so what is shown is contiguous.
    /// </summary>
    private async Task<string> BuildTranscriptAsync(
        WorkBatch batch, List<MessageRecord> messages, string target, string system, CancellationToken ct)
    {
        var batchIds = messages.Select(message => message.PublicId).ToHashSet(StringComparer.Ordinal);
        var window = await _store.Messages.ListRangeAsync(
            batch.WorkspaceName,
            batch.SessionName,
            Math.Max(1, messages[0].Seq - PrecedingContextMessages),
            messages[^1].Seq,
            ct).ConfigureAwait(false);

        var entries = window.Select(message => (message.Seq, Entry: Entry(message, batchIds.Contains(message.PublicId)))).ToList();
        var remaining = _options.MaxInputTokens
            - _tokenCounter.Count(system)
            - _tokenCounter.Count(Transcript(target, []))
            - entries.Where(e => IsTarget(e.Entry)).Sum(e => Cost(e.Entry));

        var included = entries.Where(e => IsTarget(e.Entry)).ToList();
        foreach (var context in entries.Where(e => !IsTarget(e.Entry)).OrderByDescending(e => e.Seq))
        {
            var cost = Cost(context.Entry);
            if (cost > remaining)
            {
                break;
            }

            remaining -= cost;
            included.Add(context);
        }

        return Transcript(target, [.. included.OrderBy(e => e.Seq).Select(e => e.Entry)]);
    }

    private static JsonObject Entry(MessageRecord message, bool isTarget)
    {
        var entry = new JsonObject();
        if (isTarget)
        {
            entry["id"] = message.PublicId;
        }

        entry["speaker"] = message.PeerName;
        entry["role"] = isTarget ? "target" : "context";
        entry["sent_at"] = message.CreatedAt.ToString("O", CultureInfo.InvariantCulture);
        entry["content"] = message.Content;
        return entry;
    }

    private static bool IsTarget(JsonObject entry) => entry.ContainsKey("id");

    private int Cost(JsonObject entry) => _tokenCounter.Count(entry.ToJsonString(TranscriptJson));

    private static string Transcript(string target, IReadOnlyList<JsonObject> entries) =>
        new JsonObject
        {
            ["target_peer"] = target,
            ["messages"] = new JsonArray([.. entries]),
        }.ToJsonString(TranscriptJson);

    private static string RenderCustomInstructions(ResolvedConfiguration configuration)
    {
        string?[] instructions = [configuration.CustomInstructions.Value, configuration.Reasoning.CustomInstructions.Value];
        var present = instructions.Where(text => !string.IsNullOrWhiteSpace(text)).ToList();
        return present.Count == 0
            ? string.Empty
            : $"\n## Additional instructions\n\n{string.Join("\n\n", present)}\n";
    }

    private async Task<ExtractionResult> ExtractAsync(
        string workUnitKey, string workspaceName, string system, string transcript, CancellationToken ct)
    {
        var response = await _chatClients.ForAgent(AgentName).GetResponseAsync<ExtractionResult>(
            [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, transcript)],
            AgentCallTags.CreateOptions(AgentName, workspaceName),
            cancellationToken: ct).ConfigureAwait(false);
        return response.TryGetResult(out var result)
            ? result
            : throw new InvalidOperationException(
                $"The deriver model's answer for work unit '{workUnitKey}' is not a valid ExtractionResult JSON object; the unit will be retried.");
    }

    /// <summary>
    /// Keeps the facts with non-blank content whose sources are all batch messages; drops and counts the rest. A fact
    /// with no sources is ungrounded and dropped too.
    /// </summary>
    private List<(string Content, IReadOnlyList<string> Sources)> Validate(
        string workUnitKey, ExtractionResult extraction, List<MessageRecord> messages)
    {
        var batchOrder = messages.Select((message, index) => (message.PublicId, index))
            .ToDictionary(pair => pair.PublicId, pair => pair.index, StringComparer.Ordinal);
        var proposed = extraction.Facts ?? [];
        var valid = new List<(string, IReadOnlyList<string>)>();
        foreach (var fact in proposed)
        {
            var content = fact?.Content is null ? string.Empty : ConclusionDeduplicator.Normalize(fact.Content);
            var sources = fact?.SourceMessageIds;
            if (content.Length == 0 || sources is null || sources.Count == 0 || sources.Any(id => id is null || !batchOrder.ContainsKey(id)))
            {
                continue;
            }

            valid.Add((content, [.. sources.Distinct(StringComparer.Ordinal).OrderBy(id => batchOrder[id])]));
        }

        if (valid.Count < proposed.Count)
        {
            LogInvalidFactsDropped(workUnitKey, proposed.Count - valid.Count, proposed.Count);
        }

        return valid;
    }

    private async Task<List<DerivedFact>> EmbedAsync(
        string workUnitKey, List<(string Content, IReadOnlyList<string> Sources)> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var embeddings = await _embeddings.GenerateAsync(candidates.Select(c => c.Content), cancellationToken: ct)
            .ConfigureAwait(false);
        if (embeddings.Count != candidates.Count || embeddings.Any(e => e.Vector.Length != VectorSpace.Dimensions))
        {
            throw new InvalidOperationException(
                $"The embedding generator returned {embeddings.Count} vectors for {candidates.Count} facts of work unit " +
                $"'{workUnitKey}', or vectors that are not {VectorSpace.Dimensions} long. Check the Embeddings configuration.");
        }

        return [.. candidates.Zip(embeddings, (c, e) => new DerivedFact(c.Content, e.Vector, c.Sources))];
    }

    private async Task<DerivationChanges> BuildChangesAsync(
        WorkBatch batch, string target, List<DerivedFact> facts, CancellationToken ct)
    {
        var inserts = new List<NewConclusion>();
        var corroborations = new List<string>();
        var replacements = new List<ConclusionReplacement>();
        foreach (var observer in batch.Observers)
        {
            var observed = facts.Where(fact => fact.SourceMessageIds.All(id => batch.ObserversOf(id).Contains(observer))).ToList();
            if (observed.Count == 0)
            {
                continue;
            }

            var result = await _deduplicator.DeduplicateAsync(
                batch.WorkspaceName, observer, target, batch.SessionName, observed, ct).ConfigureAwait(false);
            inserts.AddRange(result.Inserts.Select(fact => new NewConclusion(
                observer, target, batch.SessionName, ConclusionLevel.Explicit, fact.Content, fact.Embedding, [], Provenance(fact))));
            corroborations.AddRange(result.Corroborations);
            replacements.AddRange(result.Replacements);
        }

        return new DerivationChanges(batch.WorkspaceName, inserts, corroborations, replacements);
    }

    private async Task CommitAsync(WorkLease lease, DerivationChanges changes, int facts, CancellationToken ct)
    {
        await _store.Conclusions.CommitDerivationAsync(lease, changes, ct).ConfigureAwait(false);
        LogCommitted(lease.WorkUnitKey, facts, changes.Inserts.Count, changes.Corroborations.Count, changes.Replacements.Count);
    }

    private static JsonObject Provenance(DerivedFact fact) => new()
    {
        ["prompt_id"] = Prompt.Id,
        ["prompt_version"] = Prompt.Version,
        ["source_message_ids"] = new JsonArray([.. fact.SourceMessageIds.Select(id => JsonValue.Create(id))]),
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Reasoning is disabled; completing work unit {WorkUnitKey} ({Items} items) without derivation.")]
    private partial void LogReasoningDisabled(string workUnitKey, int items);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped {Dropped} of {Proposed} facts for work unit {WorkUnitKey}: blank content, or sources outside the batch.")]
    private partial void LogInvalidFactsDropped(string workUnitKey, int dropped, int proposed);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Committed work unit {WorkUnitKey}: {Facts} facts, {Inserts} inserts, {Corroborations} corroborations, {Replacements} replacements.")]
    private partial void LogCommitted(string workUnitKey, int facts, int inserts, int corroborations, int replacements);
}
