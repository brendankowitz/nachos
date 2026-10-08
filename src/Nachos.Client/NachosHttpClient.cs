using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Json;

namespace Nachos.Client;

/// <summary>
/// <see cref="INachosClient"/> over HTTP: one call per v3 wire route, System.Text.Json only, server errors mapped to
/// the Abstractions exceptions (see <see cref="ErrorMapper"/>).
/// </summary>
/// <remarks>
/// <para>
/// A status response mapped by this client, or a body failure wrapped by <see cref="RetryHandler"/>, that carried a
/// parseable <c>Retry-After</c> holds the requested delay under <see cref="NachosExceptionData.RetryAfter"/> and ends
/// its message with <c>" Retry-After: {N}s."</c> (spec §16). Other failures do not: a body that fails inside
/// <see cref="HttpClient"/> buffering on a route the handler never retries, a malformed 2xx body, and a timeout.
/// </para>
/// <para>
/// Every request carries its wire route template in <see cref="RetryHandler.RouteTemplate"/>. Retries happen only
/// when the <see cref="HttpClient"/> pipeline contains a <see cref="RetryHandler"/>; this type never retries.
/// </para>
/// <para>
/// <see cref="CreateMessagesAsync"/> always sends an <c>Idempotency-Key</c>: the caller's, or a new GUID per call.
/// A retry handler replays the same request, so the key is stable across retries of one call.
/// </para>
/// <para>
/// For an operation that returns a value, a success body that is not valid JSON (malformed, a duplicate property
/// name, or a missing required member) or has the wrong shape (an entity that is not an object, a collection or page
/// <c>items</c> that is not an array of objects, a null entry) throws <see cref="JsonException"/>; no null or
/// placeholder entity is ever returned. <see cref="SetSessionPeerConfigAsync"/> and <see cref="AddGrantAsync"/>
/// return nothing, so their success body is not interpreted at all.
/// </para>
/// <para>
/// Strict JSON data: every <see cref="JsonObject"/> the caller passes as metadata, peer configuration or filters
/// (including <see cref="MessageCreate.Metadata"/>) is converted with <see cref="StrictJsonData.ToCanonical"/> before
/// the request is built, and only that canonical copy is serialized. A value the helper rejects throws its
/// <see cref="NachosValidationException"/> before anything is sent. Plain string members (ids, message content,
/// typed configuration text) are a separate contract: they are not JSON data and are not checked here, so an unpaired
/// surrogate in one is written by the serializer as U+FFFD, giving the same request bytes as a literal U+FFFD. No
/// claim is made that the server's request identity distinguishes the two.
/// </para>
/// <para>
/// The <see cref="HttpClient"/> is borrowed, not owned: the caller disposes it. The type is safe for concurrent use.
/// </para>
/// </remarks>
public sealed class NachosHttpClient : INachosClient
{
    private const string Workspaces = "/v3/workspaces";
    private const string WorkspacesList = Workspaces + "/list";
    private const string W = Workspaces + "/{workspace_id}";
    private const string Peers = W + "/peers";
    private const string PeersList = Peers + "/list";
    private const string P = Peers + "/{peer_id}";
    private const string PeerSessions = P + "/sessions";
    private const string Sessions = W + "/sessions";
    private const string SessionsList = Sessions + "/list";
    private const string S = Sessions + "/{session_id}";
    private const string SessionPeers = S + "/peers";
    private const string SessionPeerConfig = SessionPeers + "/{peer_id}/config";
    private const string Messages = S + "/messages";
    private const string MessagesList = Messages + "/list";
    private const string MessageById = Messages + "/{message_id}";
    private const string Keys = "/v3/keys";
    private const string Grants = "/v3/admin/grants";

    private const string IdempotencyKeyHeader = "Idempotency-Key";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    // Optional object members of the wire entities (absent in the manifest's "required"): an omitted or null value
    // reads as an empty object, because the Abstractions records model them as non-null.
    private static readonly string[] MetadataAndConfiguration = ["metadata", "configuration"];
    private static readonly string[] MetadataOnly = ["metadata"];

    private readonly HttpClient _http;
    private readonly Uri _baseAddress;
    private readonly string? _apiKey;
    private readonly TimeProvider _timeProvider;

    /// <exception cref="ArgumentException">
    /// <see cref="NachosClientOptions.BaseAddress"/> is missing or relative, or the API key is not printable ASCII.
    /// </exception>
    public NachosHttpClient(HttpClient httpClient, NachosClientOptions options)
        : this(httpClient, options, TimeProvider.System)
    {
    }

    /// <param name="httpClient">The borrowed client.</param>
    /// <param name="options">Connection settings.</param>
    /// <param name="timeProvider">Clock for turning an HTTP-date <c>Retry-After</c> into a delay.</param>
    internal NachosHttpClient(HttpClient httpClient, NachosClientOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        var baseAddress = options.BaseAddress;
        if (baseAddress is null || !baseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("BaseAddress must be an absolute URI.", nameof(options));
        }

        // The message never echoes the key.
        if (options.ApiKey is { } key && (key.Length == 0 || key.Any(c => c is < '!' or > '~')))
        {
            throw new ArgumentException("ApiKey must be non-empty printable ASCII without whitespace.", nameof(options));
        }

        _http = httpClient;
        _baseAddress = baseAddress.AbsoluteUri.EndsWith('/') ? baseAddress : new Uri(baseAddress.AbsoluteUri + "/");
        _apiKey = options.ApiKey;
    }

    public async Task<Workspace> GetOrCreateWorkspaceAsync(
        string id, JsonObject? metadata = null, WorkspaceConfiguration? configuration = null, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Post, Workspaces, [], null, new WorkspaceCreate(id, Data(metadata), configuration), null, ct)
            .ConfigureAwait(false);
        return ReadEntity<Workspace>(json, MetadataAndConfiguration);
    }

    public async Task<Page<Workspace>> ListWorkspacesAsync(JsonObject? filters, PageRequest page, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Post, WorkspacesList, [], ListQuery(page), new FilterBody(Data(filters)), null, ct)
            .ConfigureAwait(false);
        return ReadPage<Workspace>(json, MetadataAndConfiguration);
    }

    public async Task<Workspace> UpdateWorkspaceAsync(
        string id, JsonObject? metadata = null, WorkspaceConfiguration? configuration = null, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Put, W, [id], null, new WorkspaceUpdate(Data(metadata), configuration), null, ct)
            .ConfigureAwait(false);
        return ReadEntity<Workspace>(json, MetadataAndConfiguration);
    }

    public async Task<Peer> GetOrCreatePeerAsync(
        string workspaceId, string id, JsonObject? metadata = null, JsonObject? configuration = null, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Post, Peers, [workspaceId], null, new PeerCreate(id, Data(metadata), Data(configuration)), null, ct)
            .ConfigureAwait(false);
        return ReadEntity<Peer>(json, MetadataAndConfiguration);
    }

    public async Task<Page<Peer>> ListPeersAsync(
        string workspaceId, PeerKind? kind, JsonObject? filters, PageRequest page, CancellationToken ct = default)
    {
        var wireKind = kind switch
        {
            null or PeerKind.Regular => null,
            PeerKind.Scope => "scope",
            PeerKind.All => "all",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown peer kind."),
        };
        var json = await SendAsync(HttpMethod.Post, PeersList, [workspaceId], ListQuery(page), new PeerGet(Data(filters), wireKind), null, ct)
            .ConfigureAwait(false);
        return ReadPage<Peer>(json, MetadataAndConfiguration);
    }

    public async Task<Peer> UpdatePeerAsync(
        string workspaceId, string id, JsonObject? metadata = null, JsonObject? configuration = null, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Put, P, [workspaceId, id], null, new PeerUpdate(Data(metadata), Data(configuration)), null, ct)
            .ConfigureAwait(false);
        return ReadEntity<Peer>(json, MetadataAndConfiguration);
    }

    public async Task<Page<Session>> ListPeerSessionsAsync(
        string workspaceId, string peerId, JsonObject? filters, PageRequest page, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Post, PeerSessions, [workspaceId, peerId], ListQuery(page), new FilterBody(Data(filters)), null, ct)
            .ConfigureAwait(false);
        return ReadPage<Session>(json, MetadataAndConfiguration);
    }

    public async Task<Session> GetOrCreateSessionAsync(
        string workspaceId,
        string id,
        JsonObject? metadata = null,
        SessionConfiguration? configuration = null,
        IReadOnlyDictionary<string, SessionPeerConfig>? peers = null,
        CancellationToken ct = default)
    {
        var body = new SessionCreate(id, Data(metadata), configuration, peers);
        var json = await SendAsync(HttpMethod.Post, Sessions, [workspaceId], null, body, null, ct).ConfigureAwait(false);
        return ReadEntity<Session>(json, MetadataAndConfiguration);
    }

    public async Task<Page<Session>> ListSessionsAsync(
        string workspaceId, JsonObject? filters, PageRequest page, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Post, SessionsList, [workspaceId], ListQuery(page), new FilterBody(Data(filters)), null, ct)
            .ConfigureAwait(false);
        return ReadPage<Session>(json, MetadataAndConfiguration);
    }

    public async Task<Session> UpdateSessionAsync(
        string workspaceId, string id, JsonObject? metadata = null, SessionConfiguration? configuration = null, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Put, S, [workspaceId, id], null, new SessionUpdate(Data(metadata), configuration), null, ct)
            .ConfigureAwait(false);
        return ReadEntity<Session>(json, MetadataAndConfiguration);
    }

    public async Task<Session> AddSessionPeersAsync(
        string workspaceId, string sessionId, IReadOnlyDictionary<string, SessionPeerConfig> peers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(peers);
        var json = await SendAsync(HttpMethod.Post, SessionPeers, [workspaceId, sessionId], null, peers, null, ct).ConfigureAwait(false);
        return ReadEntity<Session>(json, MetadataAndConfiguration);
    }

    public async Task<Session> SetSessionPeersAsync(
        string workspaceId, string sessionId, IReadOnlyDictionary<string, SessionPeerConfig> peers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(peers);
        var json = await SendAsync(HttpMethod.Put, SessionPeers, [workspaceId, sessionId], null, peers, null, ct).ConfigureAwait(false);
        return ReadEntity<Session>(json, MetadataAndConfiguration);
    }

    public async Task<Session> RemoveSessionPeersAsync(
        string workspaceId, string sessionId, IReadOnlyList<string> peerIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(peerIds);
        var json = await SendAsync(HttpMethod.Delete, SessionPeers, [workspaceId, sessionId], null, peerIds, null, ct).ConfigureAwait(false);
        return ReadEntity<Session>(json, MetadataAndConfiguration);
    }

    public async Task<Page<Peer>> ListSessionPeersAsync(
        string workspaceId, string sessionId, PageRequest page, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        // The route takes only page and size (no reverse).
        var query = string.Create(CultureInfo.InvariantCulture, $"page={page.Page}&size={page.Size}");
        var json = await SendAsync(HttpMethod.Get, SessionPeers, [workspaceId, sessionId], query, null, null, ct).ConfigureAwait(false);
        return ReadPage<Peer>(json, MetadataAndConfiguration);
    }

    public async Task<SessionPeerConfig> GetSessionPeerConfigAsync(
        string workspaceId, string sessionId, string peerId, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Get, SessionPeerConfig, [workspaceId, sessionId, peerId], null, null, null, ct)
            .ConfigureAwait(false);
        return ReadEntity<SessionPeerConfig>(json, []);
    }

    public async Task SetSessionPeerConfigAsync(
        string workspaceId, string sessionId, string peerId, SessionPeerConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await SendAsync(HttpMethod.Put, SessionPeerConfig, [workspaceId, sessionId, peerId], null, config, null, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Message>> CreateMessagesAsync(
        string workspaceId,
        string sessionId,
        IReadOnlyList<MessageCreate> messages,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (idempotencyKey is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        }

        var key = idempotencyKey ?? Guid.NewGuid().ToString("D");
        var json = await SendAsync(HttpMethod.Post, Messages, [workspaceId, sessionId], null, new MessageBatchCreate(Data(messages)), key, ct)
            .ConfigureAwait(false);
        var items = RequireArrayOfObjects(WireJson.Parse(json), nameof(Message), MetadataOnly);
        return Read<Message[]>(items);
    }

    public async Task<Page<Message>> ListMessagesAsync(
        string workspaceId, string sessionId, JsonObject? filters, PageRequest page, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Post, MessagesList, [workspaceId, sessionId], ListQuery(page), new FilterBody(Data(filters)), null, ct)
            .ConfigureAwait(false);
        return ReadPage<Message>(json, MetadataOnly);
    }

    public async Task<Message> GetMessageAsync(
        string workspaceId, string sessionId, string messageId, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Get, MessageById, [workspaceId, sessionId, messageId], null, null, null, ct)
            .ConfigureAwait(false);
        return ReadEntity<Message>(json, MetadataOnly);
    }

    public async Task<Message> UpdateMessageAsync(
        string workspaceId, string sessionId, string messageId, JsonObject? metadata, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Put, MessageById, [workspaceId, sessionId, messageId], null, new MessageUpdate(Data(metadata)), null, ct)
            .ConfigureAwait(false);
        return ReadEntity<Message>(json, MetadataOnly);
    }

    public async Task<KeyResponse> CreateKeyAsync(
        string? workspaceId = null,
        string? peerId = null,
        string? sessionId = null,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default)
    {
        // The manifest takes the scope as query parameters and no body; scope rules are the server's (422).
        var parameters = new List<string>(4);
        AddQuery(parameters, "workspace_id", workspaceId);
        AddQuery(parameters, "peer_id", peerId);
        AddQuery(parameters, "session_id", sessionId);
        AddQuery(parameters, "expires_at", expiresAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        var query = parameters.Count == 0 ? null : string.Join('&', parameters);

        var json = await SendAsync(HttpMethod.Post, Keys, [], query, null, null, ct).ConfigureAwait(false);
        return ReadEntity<KeyResponse>(json, []);
    }

    public async Task AddGrantAsync(string objectId, string? workspaceId, string role, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        ArgumentNullException.ThrowIfNull(role);
        await SendAsync(HttpMethod.Post, Grants, [], null, new GrantCreate(objectId, workspaceId, role), null, ct).ConfigureAwait(false);
    }

    /// <summary>Sends one request and returns the success body; any other status throws the mapped exception.</summary>
    private async Task<string> SendAsync(
        HttpMethod method,
        string template,
        string[] routeValues,
        string? query,
        object? body,
        string? idempotencyKey,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(method, Resolve(template, routeValues, query));
        request.Options.Set(RetryHandler.RouteTemplate, template);

        // TODO(task-12-credential): when NachosClientOptions.Credential is set, acquire an Entra token for
        // NachosClientOptions.Scopes here and send it instead of the API key.
        if (_apiKey is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add(IdempotencyKeyHeader, idempotencyKey);
        }

        if (body is not null)
        {
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), Json));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ErrorMapper.Map(response, text, $"{method} {template}", _apiKey, _timeProvider);
        }

        return text;
    }

    /// <summary>
    /// Fills each <c>{parameter}</c> segment of <paramref name="template"/> with the next escaped route value and
    /// resolves the result beneath the base address.
    /// </summary>
    private Uri Resolve(string template, string[] routeValues, string? query)
    {
        var path = new StringBuilder();
        var next = 0;
        foreach (var segment in template.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (path.Length > 0)
            {
                path.Append('/');
            }

            if (segment.StartsWith('{'))
            {
                var value = routeValues[next++];

                // "." and ".." would be collapsed as dot-segments and retarget the request at another route.
                if (string.IsNullOrEmpty(value) || value is "." or "..")
                {
                    throw new ArgumentException($"Route value {segment} must be a non-empty id other than '.' or '..'.", segment[1..^1]);
                }

                path.Append(Uri.EscapeDataString(value));
            }
            else
            {
                path.Append(segment);
            }
        }

        if (query is not null)
        {
            path.Append('?').Append(query);
        }

        return new Uri(_baseAddress, path.ToString());
    }

    private static string ListQuery(PageRequest page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return string.Create(
            CultureInfo.InvariantCulture, $"page={page.Page}&size={page.Size}&reverse={(page.Reverse ? "true" : "false")}");
    }

    private static void AddQuery(List<string> parameters, string name, string? value)
    {
        if (value is not null)
        {
            parameters.Add(name + "=" + Uri.EscapeDataString(value));
        }
    }

    // Success bodies are checked for shape here, at the protocol boundary, before deserialization: an entity is a JSON
    // object, a collection is an array of objects, and a page is an object whose "items" is such an array. Anything else
    // (a null entry included) throws JsonException, so no null or fabricated entity is ever returned.

    private static T ReadEntity<T>(string json, string[] objectMembers) =>
        Read<T>(RequireObject(WireJson.Parse(json), typeof(T).Name, objectMembers));

    private static Page<T> ReadPage<T>(string json, string[] objectMembers)
    {
        var page = RequireObject(WireJson.Parse(json), $"a page of {typeof(T).Name}", []);
        RequireArrayOfObjects(page["items"], typeof(T).Name, objectMembers);
        return Read<Page<T>>(page);
    }

    private static JsonArray RequireArrayOfObjects(JsonNode? node, string itemName, string[] objectMembers)
    {
        if (node is not JsonArray items)
        {
            throw new JsonException($"Expected an array of {itemName} but the response had {Kind(node)}.");
        }

        for (var i = 0; i < items.Count; i++)
        {
            RequireObject(items[i], $"{itemName} at index {i}", objectMembers);
        }

        return items;
    }

    /// <summary>
    /// The node as an object, with each of <paramref name="objectMembers"/> that is absent or null set to <c>{}</c>
    /// (optional wire members the Abstractions records model as non-null).
    /// </summary>
    private static JsonObject RequireObject(JsonNode? node, string what, string[] objectMembers)
    {
        if (node is not JsonObject obj)
        {
            throw new JsonException($"Expected {what} to be a JSON object but the response had {Kind(node)}.");
        }

        foreach (var member in objectMembers)
        {
            if (obj[member] is null)
            {
                obj[member] = new JsonObject();
            }
        }

        return obj;
    }

    private static string Kind(JsonNode? node) => node is null ? "null" : node.GetValueKind().ToString();

    // Callers pass a validated object or array, so a null result cannot occur; the check keeps that explicit.
    private static T Read<T>(JsonNode node) =>
        node.Deserialize<T>(Json) ?? throw new JsonException($"Expected a {typeof(T).Name} but the response body was null.");

    // Strict JSON data (StrictJsonData): each caller JsonNode in a request body is replaced by its canonical copy, and
    // only that copy is serialized, so the caller's tree (getters, converters, later mutation) never reaches the wire.
    // A rejected value throws the helper's NachosValidationException before anything is sent.
    private static JsonObject? Data(JsonObject? value) => (JsonObject?)StrictJsonData.ToCanonical(value);

    private static MessageCreate[] Data(IReadOnlyList<MessageCreate> messages)
    {
        var canonical = new MessageCreate[messages.Count];
        for (var i = 0; i < canonical.Length; i++)
        {
            var message = messages[i] ?? throw new ArgumentException($"messages[{i}] is null.", nameof(messages));
            canonical[i] = message with { Metadata = Data(message.Metadata) };
        }

        return canonical;
    }
}
