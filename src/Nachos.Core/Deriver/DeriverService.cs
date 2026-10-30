using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
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
/// Each work item's snapshot (<see cref="RepresentationPayload"/>) is authoritative: its observers, never the current
/// memberships, and its message-level <c>reasoning</c>, which overrides the session and workspace as they are now. A
/// message whose reasoning is now disabled is shown as context only. A fact reaches an observer only if that observer saw
/// every message the fact is sourced from.
/// </para>
/// <para>
/// When the workspace, session or messages are gone (<see cref="NotFoundException"/> while reading), the batch completes
/// with no changes instead of failing until it is dead-lettered.
/// </para>
/// <para>Scoped, because it resolves configuration through the scoped <see cref="IConfigurationResolver"/>.</para>
/// </remarks>
public sealed partial class DeriverService : IWorkHandler
{
    public const string AgentName = "deriver";

    /// <summary>
    /// How many non-batch messages, ending at the batch's last message, are read as context (budget permitting): the
    /// messages between and just before the batch's messages, newest first.
    /// </summary>
    private const int ContextMessages = 10;

    private const string PromptResource = "Nachos.Core.Deriver.Prompts.deriver.v1.md";

    private static readonly PromptTemplate Prompt = PromptTemplate.LoadEmbedded(typeof(DeriverService).Assembly, PromptResource);

    // The transcript is model input, not HTML, so non-ASCII text stays readable instead of becoming \u escapes. Quotes and
    // backslashes are still escaped, so message content can never break out of its JSON string.
    private static readonly JsonSerializerOptions TranscriptJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IMemoryStore _store;
    private readonly NachosChatClients _chatClients;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly IConfigurationResolver _configurationResolver;
    private readonly ITokenCounter _tokenCounter;
    private readonly ConclusionDeduplicator _deduplicator;
    private readonly DeriverOptions _options;
    private readonly WorkerMetrics _metrics;
    private readonly ILogger<DeriverService> _logger;
    private readonly string _embeddingModel;

    public DeriverService(
        IMemoryStore store,
        NachosChatClients chatClients,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        IConfigurationResolver configurationResolver,
        ITokenCounter tokenCounter,
        ConclusionDeduplicator deduplicator,
        IOptions<NachosOptions> options,
        WorkerMetrics metrics,
        ILogger<DeriverService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(chatClients);
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(configurationResolver);
        ArgumentNullException.ThrowIfNull(tokenCounter);
        ArgumentNullException.ThrowIfNull(deduplicator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _chatClients = chatClients;
        _embeddings = embeddings;
        _configurationResolver = configurationResolver;
        _tokenCounter = tokenCounter;
        _deduplicator = deduplicator;
        _options = options.Value.Deriver;
        _metrics = metrics;
        _logger = logger;
        _embeddingModel = EmbeddingModelName.Of(embeddings);
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

        Derivation derivation;
        try
        {
            derivation = await DeriveAsync(lease.WorkUnitKey, batch, cancellationToken).ConfigureAwait(false);
        }
        catch (NotFoundException ex)
        {
            LogSourceGone(lease.WorkUnitKey, ex.Message);
            derivation = new Derivation(batch.Nothing(), 0, 0);
        }

        await _store.Conclusions.CommitDerivationAsync(lease, derivation.Changes, cancellationToken).ConfigureAwait(false);
        _metrics.RecordDerivation(derivation.Facts, derivation.ObserverFacts, derivation.Changes.Inserts.Count);
        LogCommitted(
            lease.WorkUnitKey,
            derivation.Facts,
            derivation.Changes.Inserts.Count,
            derivation.Changes.Corroborations.Count,
            derivation.Changes.Replacements.Count);
    }

    /// <summary>Computes the batch's changes without writing anything.</summary>
    private async Task<Derivation> DeriveAsync(string workUnitKey, WorkBatch batch, CancellationToken ct)
    {
        var configuration = await ResolveConfigurationAsync(batch, ct).ConfigureAwait(false);
        var sessionEnabled = configuration.Reasoning.Enabled.Value ?? true;
        var targetIds = batch.MessageIds
            .Where(id => batch.WorkOf(id).ReasoningEnabled ?? sessionEnabled)
            .ToHashSet(StringComparer.Ordinal);
        if (targetIds.Count == 0)
        {
            LogReasoningDisabled(workUnitKey, batch.MessageIds.Count);
            return new Derivation(batch.Nothing(), 0, 0);
        }

        // A blank message states nothing, so it is never sent to the model; its item completes with the batch.
        var targets = (await LoadTargetsAsync(batch, targetIds, ct).ConfigureAwait(false))
            .Where(message => !string.IsNullOrWhiteSpace(message.Content))
            .ToList();
        if (targets.Count == 0)
        {
            return new Derivation(batch.Nothing(), 0, 0);
        }

        var target = targets[0].PeerName;
        if (targets.Any(message => !string.Equals(message.PeerName, target, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Work unit '{workUnitKey}' holds messages from more than one sender; a representation unit has one.");
        }

        var system = Prompt.Render(new Dictionary<string, string>
        {
            ["target_peer"] = target,
            ["custom_instructions"] = RenderCustomInstructions(configuration),
        });
        var transcript = await BuildTranscriptAsync(batch, targets, target, MessageInstructions(batch, targets), system, ct)
            .ConfigureAwait(false);
        var extraction = await ExtractAsync(workUnitKey, batch.WorkspaceName, system, transcript, ct).ConfigureAwait(false);
        var candidates = Validate(workUnitKey, extraction, targets);
        var facts = await EmbedAsync(workUnitKey, candidates, ct).ConfigureAwait(false);
        var (changes, observerFacts) = await BuildChangesAsync(batch, target, facts, ct).ConfigureAwait(false);
        return new Derivation(changes, facts.Count, observerFacts);
    }

    private async Task<ResolvedConfiguration> ResolveConfigurationAsync(WorkBatch batch, CancellationToken ct)
    {
        var workspace = await _store.Workspaces.GetAsync(batch.WorkspaceName, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Workspace '{batch.WorkspaceName}' not found.");
        var session = await _store.Sessions.GetAsync(batch.WorkspaceName, batch.SessionName, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Session '{batch.SessionName}' not found in workspace '{batch.WorkspaceName}'.");
        // Message-level reasoning is not stored with the message; each item's snapshot applies it per message.
        return _configurationResolver.Resolve(workspace.Configuration, session.Configuration);
    }

    /// <summary>
    /// The target messages in <c>Seq</c> order, read in one filtered list (paged at the store's maximum page size). A
    /// message deleted since it was enqueued is left out.
    /// </summary>
    private async Task<List<MessageRecord>> LoadTargetsAsync(WorkBatch batch, HashSet<string> targetIds, CancellationToken ct)
    {
        var filter = new FilterNode.Field(
            FilterColumns.PublicId, FilterOp.In, new JsonArray([.. targetIds.Select(id => JsonValue.Create(id))]));
        var targets = new List<MessageRecord>();
        for (var page = 1; ; page++)
        {
            var result = await _store.Messages.ListAsync(
                batch.WorkspaceName, batch.SessionName, filter, new PageRequest(page, PageRequest.MaxSize), ct).ConfigureAwait(false);
            targets.AddRange(result.Items);
            if (result.Items.Count < PageRequest.MaxSize)
            {
                return targets;
            }
        }
    }

    /// <summary>
    /// The user message: the message-level instructions, the target messages marked <c>target</c>, plus up to
    /// <see cref="ContextMessages"/> other messages ending at the last target, newest first while they fit
    /// <see cref="DeriverOptions.MaxInputTokens"/>. The targets are always included, even over budget, because they are the
    /// subject of the request; context stops at the first message that does not fit, so what is shown is contiguous.
    /// </summary>
    private async Task<string> BuildTranscriptAsync(
        WorkBatch batch,
        List<MessageRecord> targets,
        string target,
        IReadOnlyList<string> instructions,
        string system,
        CancellationToken ct)
    {
        var targetIds = targets.Select(message => message.PublicId).ToHashSet(StringComparer.Ordinal);
        var lastSeq = targets[^1].Seq;
        // Seq values are unique per session, so this range holds at most the batch plus ContextMessages messages.
        var window = await _store.Messages.ListRangeAsync(
            batch.WorkspaceName,
            batch.SessionName,
            Math.Max(1, lastSeq - batch.MessageIds.Count - ContextMessages + 1),
            lastSeq,
            ct).ConfigureAwait(false);

        var remaining = _options.MaxInputTokens
            - _tokenCounter.Count(system)
            - _tokenCounter.Count(Transcript(target, instructions, []));
        var included = targets.Select(message => (message.Seq, Entry: Entry(message, isTarget: true))).ToList();
        remaining -= included.Sum(e => Cost(e.Entry));

        var context = window.Where(message => !targetIds.Contains(message.PublicId)).OrderByDescending(message => message.Seq);
        foreach (var message in context)
        {
            var entry = Entry(message, isTarget: false);
            var cost = Cost(entry);
            if (cost > remaining)
            {
                break;
            }

            remaining -= cost;
            included.Add((message.Seq, entry));
        }

        return Transcript(target, instructions, [.. included.OrderBy(e => e.Seq).Select(e => e.Entry)]);
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

    private int Cost(JsonObject entry) => _tokenCounter.Count(entry.ToJsonString(TranscriptJson));

    /// <summary>
    /// The user message's JSON. Message-level instructions arrive with each message, from whoever posts it, so they travel
    /// here as data under <c>message_instructions</c> (present only when there are some), never in the system prompt.
    /// </summary>
    private static string Transcript(string target, IReadOnlyList<string> instructions, IReadOnlyList<JsonObject> entries)
    {
        var transcript = new JsonObject { ["target_peer"] = target };
        if (instructions.Count > 0)
        {
            transcript["message_instructions"] = new JsonArray([.. instructions.Select(text => JsonValue.Create(text))]);
        }

        transcript["messages"] = new JsonArray([.. entries]);
        return transcript.ToJsonString(TranscriptJson);
    }

    /// <summary>Each distinct non-blank message-level instruction of the target messages, in ordinal order.</summary>
    private static List<string> MessageInstructions(WorkBatch batch, List<MessageRecord> targets) =>
        targets
            .Select(message => batch.WorkOf(message.PublicId).CustomInstructions)
            .OfType<string>()
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>The resolved workspace/session general and reasoning instructions, for the system prompt.</summary>
    private static string RenderCustomInstructions(ResolvedConfiguration configuration)
    {
        var present = new[] { configuration.CustomInstructions.Value, configuration.Reasoning.CustomInstructions.Value }
            .OfType<string>()
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();
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
                $"The deriver model's answer for work unit '{workUnitKey}' is not a valid ExtractionResult JSON object " +
                $"(finish reason: {response.FinishReason?.Value ?? "none"}); the unit will be retried.");
    }

    /// <summary>
    /// Keeps the facts with non-blank content whose sources are all target messages; drops and counts the rest. A fact
    /// with no sources is ungrounded and dropped too.
    /// </summary>
    private List<(string Content, IReadOnlyList<string> Sources)> Validate(
        string workUnitKey, ExtractionResult extraction, List<MessageRecord> targets)
    {
        var order = targets.Select((message, index) => (message.PublicId, index))
            .ToDictionary(pair => pair.PublicId, pair => pair.index, StringComparer.Ordinal);
        if (extraction.Facts is null)
        {
            LogFactsMissing(workUnitKey);
        }

        var proposed = extraction.Facts ?? [];
        var valid = new List<(string, IReadOnlyList<string>)>();
        foreach (var fact in proposed)
        {
            var content = fact?.Content is null ? string.Empty : ConclusionDeduplicator.Normalize(fact.Content);
            var sources = fact?.SourceMessageIds;
            if (content.Length == 0 || sources is null || sources.Count == 0 || sources.Any(id => id is null || !order.ContainsKey(id)))
            {
                continue;
            }

            valid.Add((content, [.. sources.Distinct(StringComparer.Ordinal).OrderBy(id => order[id])]));
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

    /// <returns>The changes, and the facts summed over the observers each one reached.</returns>
    private async Task<(DerivationChanges Changes, int ObserverFacts)> BuildChangesAsync(
        WorkBatch batch, string target, List<DerivedFact> facts, CancellationToken ct)
    {
        var inserts = new List<NewConclusion>();
        var corroborations = new List<string>();
        var replacements = new List<ConclusionReplacement>();
        var observerFacts = 0;
        var observers = facts
            .SelectMany(fact => fact.SourceMessageIds)
            .SelectMany(id => batch.WorkOf(id).Observers)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        foreach (var observer in observers)
        {
            var observed = facts
                .Where(fact => fact.SourceMessageIds.All(id => batch.WorkOf(id).Observers.Contains(observer, StringComparer.Ordinal)))
                .ToList();
            if (observed.Count == 0)
            {
                continue;
            }

            observerFacts += observed.Count;
            var result = await _deduplicator.DeduplicateAsync(
                batch.WorkspaceName, observer, target, batch.SessionName, observed, ct).ConfigureAwait(false);
            inserts.AddRange(result.Inserts.Select(fact => new NewConclusion(
                observer, target, batch.SessionName, ConclusionLevel.Explicit, fact.Content, fact.Embedding, [], Provenance(fact))));
            corroborations.AddRange(result.Corroborations);
            replacements.AddRange(result.Replacements);
        }

        return (new DerivationChanges(batch.WorkspaceName, inserts, corroborations, replacements), observerFacts);
    }

    private JsonObject Provenance(DerivedFact fact) => new()
    {
        ["prompt_id"] = Prompt.Id,
        ["prompt_version"] = Prompt.Version,
        ["source_message_ids"] = new JsonArray([.. fact.SourceMessageIds.Select(id => JsonValue.Create(id))]),
        [VectorSpace.ModelMetadataKey] = _embeddingModel,
    };

    private sealed record Derivation(DerivationChanges Changes, int Facts, int ObserverFacts);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reasoning is disabled for every message; completing work unit {WorkUnitKey} ({Items} items) without derivation.")]
    private partial void LogReasoningDisabled(string workUnitKey, int items);

    [LoggerMessage(Level = LogLevel.Information, Message = "Completing work unit {WorkUnitKey} without derivation: {Reason}")]
    private partial void LogSourceGone(string workUnitKey, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped {Dropped} of {Proposed} facts for work unit {WorkUnitKey}: blank content, or sources outside the batch.")]
    private partial void LogInvalidFactsDropped(string workUnitKey, int dropped, int proposed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The deriver model's answer for work unit {WorkUnitKey} has no 'facts' field; treating it as no facts.")]
    private partial void LogFactsMissing(string workUnitKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Committed work unit {WorkUnitKey}: {Facts} facts, {Inserts} inserts, {Corroborations} corroborations, {Replacements} replacements.")]
    private partial void LogCommitted(string workUnitKey, int facts, int inserts, int corroborations, int replacements);
}
