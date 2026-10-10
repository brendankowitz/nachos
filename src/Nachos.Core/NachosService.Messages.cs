using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Json;
using Nachos.Core.Configuration;
using Nachos.Core.Idempotency;
using Nachos.Core.Validation;

namespace Nachos.Core;

public sealed partial class NachosService
{
    // Metadata is independently limited to64 containers. Its envelope adds root, messages array and message object.
    private static readonly JsonSerializerOptions MessageJson = new() { MaxDepth = StrictJsonData.DefaultMaxDepth + 3 };

    /// <summary>
    /// Projects a messages envelope with every declared field (content, peer_id, metadata, configuration, created_at),
    /// including explicit nulls. Configuration likewise includes all its declared nullable fields.
    /// The original metadata is normalized before serialization; this projection shares the captured-response path.
    /// </summary>
    public async Task<IReadOnlyList<Message>> CreateMessagesAsync(string workspaceId, string sessionId,
        IReadOnlyList<MessageCreate> messages, string? idempotencyKey = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        JsonArray? items = null;
        if (messages is not null)
        {
            items = [];
            foreach (var message in messages)
            {
                // Keep typed ID failures domain-shaped before raw wire-schema checks classify nulls and missing fields.
                if (message is not null) IdValidator.Validate(message.PeerId, "peer_id");
                items.Add(message is null ? null : new JsonObject
                {
                    ["content"] = StrictJsonData.ToCanonical(JsonValue.Create(message.Content)),
                    ["peer_id"] = message.PeerId,
                    ["metadata"] = Canonical(message.Metadata),
                    ["configuration"] = ConfigurationJson.Project(message.Configuration),
                    ["created_at"] = message.CreatedAt,
                });
            }
        }
        var body = JsonSerializer.SerializeToElement(new JsonObject { ["messages"] = items }, MessageJson);
        var response = await CreateMessagesResponseAsync(workspaceId, sessionId, body, idempotencyKey, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<Message[]>(response.Body, MessageJson)
            ?? throw new InvalidOperationException("The captured message response must be an array.");
    }

    /// <summary>
    /// One validation, token-counting, atomic append and replay path for trusted in-process and authorized HTTP callers.
    /// The unchanged original envelope determines identity, including unknown fields and null-versus-omitted fields.
    /// All string values and property names are Unicode-validated before hashing, token counting or store access.
    /// Both HTTP aliases use POST /v3/workspaces/{w}/sessions/{s}/messages with the resolved w and s values.
    /// </summary>
    public async Task<CapturedResponse> CreateMessagesResponseAsync(string workspaceId, string sessionId,
        JsonElement requestBody, string? idempotencyKey, CancellationToken ct)
    {
        Validate(ct, (workspaceId, "workspace_id"), (sessionId, "session_id"));
        if (idempotencyKey is not null &&
            (idempotencyKey.Length is < 1 or > 255 || idempotencyKey.Any(c => !char.IsAscii(c))))
        {
            throw new NachosValidationException("Idempotency-Key must contain 1 to 255 ASCII characters.");
        }
        JsonUnicodeValidator.Validate(requestBody);
        var messages = MessageEnvelopeReader.Read(requestBody, MessageJson);
        validator.ValidateMessages(messages);
        var hash = idempotencyKey is null ? null : RequestHasher.Hash("POST",
            "/v3/workspaces/{w}/sessions/{s}/messages",
            new Dictionary<string, string> { ["w"] = workspaceId, ["s"] = sessionId },
            CanonicalJson.Serialize(requestBody));
        await RequireSession(workspaceId, sessionId, ct).ConfigureAwait(false);

        NewMessage[]? pending = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (idempotencyKey is not null)
            {
                var existing = await store.Idempotency.TryGetAsync(workspaceId, idempotencyKey, ct).ConfigureAwait(false);
                if (existing is not null)
                {
                    if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
                    {
                        throw new IdempotencyKeyReusedException("Idempotency-Key was already used with a different request.");
                    }
                    return new(existing.ResponseStatus, existing.ResponseBody);
                }
            }

            pending ??= messages.Select(m => new NewMessage(m.PeerId, m.Content, tokenCounter.Count(m.Content),
                m.Metadata, m.CreatedAt)).ToArray();
            string? captured = null;
            var write = idempotencyKey is null ? null : new IdempotencyWrite(idempotencyKey, hash!, 201,
                rows => captured = SerializeMessages(rows), TimeSpan.FromHours(24));
            try
            {
                var rows = await store.Messages.AppendAsync(workspaceId, sessionId, pending, write, ct).ConfigureAwait(false);
                return new(201, write is null ? SerializeMessages(rows) :
                    captured ?? throw new InvalidOperationException("The store did not capture the response inside the append."));
            }
            catch (IdempotencyDuplicateException) when (idempotencyKey is not null)
            {
                // The next read returns the winner, or null if it expired/disappeared. Only a live hash can conflict.
                await Task.Yield();
            }
        }
    }

    private static string SerializeMessages(IReadOnlyList<MessageRecord> rows) =>
        JsonSerializer.Serialize(rows.Select(Map).ToArray(), MessageJson);
}
