using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;

namespace Nachos.Client;

/// <summary>
/// Maps a non-success response (spec §9: <c>{"detail": "..."}</c> or the HTTPValidationError
/// <c>{"detail": [...]}</c>, plus RFC 9457 <c>type</c>/<c>title</c>/<c>status</c>) to the Abstractions exception
/// the in-process <see cref="INachosClient"/> would throw.
/// </summary>
/// <remarks>
/// Server-supplied text is untrusted: if it echoes the API key back, the key is replaced with
/// <see cref="Redacted"/>, and then the text is cut to <see cref="MaxMessageLength"/> (redaction first, so a key
/// straddling the cut is never partly kept). A body that is not valid JSON (including one with duplicate property
/// names) is treated as having no detail, and the status alone picks the exception.
/// </remarks>
internal static class ErrorMapper
{
    /// <summary>
    /// The last segment of the RFC 9457 <c>type</c> that marks an Idempotency-Key reused with a different request.
    /// The status (422) and string <c>detail</c> are shared with domain validation, so <c>type</c> is the only
    /// machine-readable discriminator. Matching the last segment (after <c>/</c>, <c>:</c> or <c>#</c>) accepts
    /// <see cref="IdempotencyKeyReusedTypeUri"/>.
    /// </summary>
    public const string IdempotencyKeyReusedType = "idempotency-key-reused";

    /// <summary>
    /// The full <c>type</c> the server is expected to send (title "Unprocessable Entity", status 422). Proposed for a
    /// public constant in Nachos.Abstractions shared by the API exception handler and this mapper.
    /// </summary>
    public const string IdempotencyKeyReusedTypeUri = "urn:nachos:problem:" + IdempotencyKeyReusedType;

    public const string Redacted = "[redacted]";

    /// <summary>Longest exception text taken from a server body, including <see cref="TruncationMarker"/>.</summary>
    public const int MaxMessageLength = 2048;

    public const string TruncationMarker = "…[truncated]";

    public static Exception Map(HttpResponseMessage response, string body, string operation, string? secret)
    {
        var status = response.StatusCode;
        var (detail, errors, type) = Parse(body);
        var fallback = string.Create(
            CultureInfo.InvariantCulture, $"Nachos {operation} returned {(int)status} {response.ReasonPhrase}.");
        var message = Sanitize(detail ?? fallback, secret);

        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new AuthException(message),
            HttpStatusCode.NotFound => new NotFoundException(message),
            HttpStatusCode.Conflict => new ConflictException(message),
            HttpStatusCode.UnprocessableEntity when errors is not null =>
                new RequestValidationException([.. errors.Select(e => e with { Msg = Sanitize(e.Msg, secret) })]),
            HttpStatusCode.UnprocessableEntity when IsIdempotencyKeyReused(type) => new IdempotencyKeyReusedException(message),
            HttpStatusCode.UnprocessableEntity => new NachosValidationException(message),
            _ => new HttpRequestException(
                Sanitize(detail is null ? fallback : $"{fallback} {detail}", secret), inner: null, status),
        };
    }

    private static string Sanitize(string text, string? secret)
    {
        var redacted = string.IsNullOrEmpty(secret) ? text : text.Replace(secret, Redacted, StringComparison.Ordinal);
        return redacted.Length <= MaxMessageLength
            ? redacted
            : string.Concat(redacted.AsSpan(0, MaxMessageLength - TruncationMarker.Length), TruncationMarker);
    }

    private static bool IsIdempotencyKeyReused(string? type) =>
        type is not null &&
        type[(type.LastIndexOfAny(['/', ':', '#']) + 1)..] == IdempotencyKeyReusedType;

    private static (string? Detail, IReadOnlyList<ValidationError>? Errors, string? Type) Parse(string body)
    {
        JsonObject? root;
        try
        {
            root = WireJson.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return (null, null, null);
        }

        if (root is null)
        {
            return (null, null, null);
        }

        var type = root["type"] is JsonValue t && t.TryGetValue<string>(out var typeText) ? typeText : null;
        return root["detail"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var text) => (text, null, type),
            JsonArray items => (null, ParseErrors(items), type),
            _ => (null, null, type),
        };
    }

    // Null when any entry lacks the required loc/msg/type, so a malformed body falls back to the status mapping.
    private static ValidationError[]? ParseErrors(JsonArray items)
    {
        var errors = new ValidationError[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not JsonObject item ||
                item["loc"] is not JsonArray loc ||
                item["msg"] is not JsonValue msg || !msg.TryGetValue<string>(out var msgText) ||
                item["type"] is not JsonValue kind || !kind.TryGetValue<string>(out var kindText))
            {
                return null;
            }

            errors[i] = new ValidationError([.. loc.Select(LocPart)], msgText, kindText);
        }

        return errors;
    }

    // FastAPI loc entries are member names (strings) or array indexes (integers).
    private static object LocPart(JsonNode? part) => part switch
    {
        JsonValue v when v.TryGetValue<string>(out var name) => name,
        JsonValue v when v.TryGetValue<int>(out var index) => index,
        _ => part?.ToJsonString() ?? "null",
    };
}
