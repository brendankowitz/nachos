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
/// Server-supplied text is untrusted: if it echoes the call's bearer value or API key back (as text or as hex, see
/// <see cref="RedactionSecrets"/>), it is replaced with <see cref="Redacted"/>, and then the text is cut to <see cref="MaxMessageLength"/> (redaction first, so a key
/// straddling the cut is never partly kept). A validation error's <c>loc</c> part that is an object or an array is
/// shown as JSON text whose strings were redacted before encoding, so an echo is caught even where the encoding would
/// escape one of its characters. A body that is not valid JSON (including one with duplicate property names) is
/// treated as having no detail, and the status alone picks the exception.
/// </remarks>
internal static class ErrorMapper
{
    public const string Redacted = "[redacted]";

    /// <summary>Longest exception text taken from a server body, including <see cref="TruncationMarker"/>.</summary>
    public const int MaxMessageLength = 2048;

    public const string TruncationMarker = "…[truncated]";

    /// <summary>
    /// Validation errors kept from one response. When there are more, one extra marker entry (empty <c>loc</c>,
    /// <c>type</c> <see cref="OmittedErrorsType"/>) states how many were dropped; the exception type stays
    /// <see cref="RequestValidationException"/> so callers handle it the same way. Entries past the cap are counted
    /// but not inspected.
    /// </summary>
    public const int MaxValidationErrors = 100;

    /// <summary>
    /// <c>loc</c> components kept per validation error. When there are more, one extra string component (starting with
    /// <see cref="TruncationMarker"/>) states how many were dropped. Each string component is redacted and bounded like
    /// the message.
    /// </summary>
    public const int MaxLocComponents = 32;

    /// <summary>The <c>type</c> of the marker entry that counts the validation errors not kept.</summary>
    public const string OmittedErrorsType = "nachos_client.errors_omitted";

    /// <remarks>
    /// When the response carried a parseable <c>Retry-After</c>, the exception holds the delay under
    /// <see cref="NachosExceptionData.RetryAfter"/> and its message ends with <c>" Retry-After: {N}s."</c> (spec §16),
    /// whatever the status. The suffix is appended after truncation and counted in <see cref="MaxMessageLength"/>.
    /// <see cref="RequestValidationException"/> has a fixed message, so it gets the data entry only, and a
    /// <see cref="NachosValidationException"/>'s <see cref="NachosValidationException.Detail"/> includes the suffix.
    /// </remarks>
    public static Exception Map(HttpResponseMessage response, string body, string operation, RedactionSecrets secrets, TimeProvider clock)
    {
        var status = response.StatusCode;
        var (detail, parsed, type) = Parse(body);
        var fallback = string.Create(
            CultureInfo.InvariantCulture, $"Nachos {operation} returned {(int)status} {response.ReasonPhrase}.");
        var retryAfter = RetryAfterHeader.Delay(response, clock);
        var suffix = retryAfter is { } delay ? RetryAfterHeader.Suffix(delay) : string.Empty;
        string Message(string text) => Sanitize(text, secrets, MaxMessageLength - suffix.Length) + suffix;

        Exception exception = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new AuthException(Message(detail ?? fallback)),
            HttpStatusCode.NotFound => new NotFoundException(Message(detail ?? fallback)),
            HttpStatusCode.Conflict => new ConflictException(Message(detail ?? fallback)),
            HttpStatusCode.UnprocessableEntity when parsed is { } errors => new RequestValidationException(Sanitize(errors, secrets)),
            HttpStatusCode.UnprocessableEntity when IsIdempotencyKeyReused(type) => new IdempotencyKeyReusedException(Message(detail ?? fallback)),
            HttpStatusCode.UnprocessableEntity => new NachosValidationException(Message(detail ?? fallback)),
            _ => new HttpRequestException(Message(detail is null ? fallback : $"{fallback} {detail}"), inner: null, status),
        };
        return RetryAfterHeader.WithDelay(exception, retryAfter);
    }

    /// <summary><paramref name="text"/> with the secrets redacted, then cut to <paramref name="maxLength"/>.</summary>
    private static string Sanitize(string text, RedactionSecrets secrets, int maxLength = MaxMessageLength) =>
        Bound(secrets.Redact(text), maxLength);

    /// <summary>
    /// <paramref name="text"/> cut to <paramref name="maxLength"/>, the cut marked with <see cref="TruncationMarker"/>
    /// (counted in the length) and never splitting a surrogate pair.
    /// </summary>
    internal static string Bound(string text, int maxLength = MaxMessageLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = maxLength - TruncationMarker.Length;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--; // never keep half of a surrogate pair
        }

        return string.Concat(text.AsSpan(0, cut), TruncationMarker);
    }

    // Every server-supplied string (msg, type, string loc parts, and the strings inside an object or array loc part) is
    // redacted and bounded like the detail text.
    private static ValidationError[] Sanitize((ValidationError[] Kept, int Omitted) errors, RedactionSecrets secrets)
    {
        var sanitized = errors.Kept
            .Select(e => new ValidationError(
                [.. e.Loc.Select(part => part switch
                {
                    string text => Sanitize(text, secrets),
                    JsonNode node => Sanitize(RedactedNode(node, secrets)!.ToJsonString(), secrets),
                    _ => part,
                })],
                Sanitize(e.Msg, secrets),
                Sanitize(e.Type, secrets)))
            .ToList();
        if (errors.Omitted > 0)
        {
            sanitized.Add(new ValidationError(
                [],
                string.Create(CultureInfo.InvariantCulture, $"{errors.Omitted} more validation errors were omitted by the client."),
                OmittedErrorsType));
        }

        return [.. sanitized];
    }

    // The status (422) and string detail are shared with domain validation, so the RFC 9457 type is the only
    // machine-readable discriminator. Spec §16: matched exactly against the shared constant, never by suffix.
    private static bool IsIdempotencyKeyReused(string? type) =>
        string.Equals(type, ProblemTypes.IdempotencyKeyReused, StringComparison.Ordinal);

    private static (string? Detail, (ValidationError[] Kept, int Omitted)? Errors, string? Type) Parse(string body)
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

    // Null when any kept entry lacks the required loc/msg/type, so a malformed body falls back to the status mapping.
    private static (ValidationError[] Kept, int Omitted)? ParseErrors(JsonArray items)
    {
        var errors = new ValidationError[Math.Min(items.Count, MaxValidationErrors)];
        for (var i = 0; i < errors.Length; i++)
        {
            if (items[i] is not JsonObject item ||
                item["loc"] is not JsonArray loc ||
                item["msg"] is not JsonValue msg || !msg.TryGetValue<string>(out var msgText) ||
                item["type"] is not JsonValue kind || !kind.TryGetValue<string>(out var kindText))
            {
                return null;
            }

            errors[i] = new ValidationError(Loc(loc), msgText, kindText);
        }

        return (errors, items.Count - errors.Length);
    }

    // Components past the cap are counted, not read; the marker is a string so it is redacted and bounded like the rest.
    private static object[] Loc(JsonArray loc)
    {
        var kept = loc.Take(MaxLocComponents).Select(LocPart);
        return loc.Count <= MaxLocComponents
            ? [.. kept]
            : [.. kept, string.Create(CultureInfo.InvariantCulture, $"{TruncationMarker} {loc.Count - MaxLocComponents} more loc components")];
    }

    // FastAPI loc entries are member names (strings) or array indexes (integers). Anything else (an object, an array, a
    // float, a bool) is kept as a node here and shown as JSON text by Sanitize, once its strings are redacted.
    private static object LocPart(JsonNode? part) => part switch
    {
        JsonValue v when v.TryGetValue<string>(out var name) => name,
        JsonValue v when v.TryGetValue<int>(out var index) => index,
        null => "null",
        _ => part,
    };

    // The strings of a loc part are redacted before it is encoded as JSON text: encoding escapes characters an echoed
    // key may contain (+ < > & ' "), and the plain match on the encoded text would then miss it. Property names that
    // redact to the same text collapse into one member. Depth is bounded by WireJson.MaxDepth.
    private static JsonNode? RedactedNode(JsonNode? node, RedactionSecrets secrets)
    {
        switch (node)
        {
            case JsonObject obj:
                var members = new JsonObject();
                foreach (var (name, value) in obj)
                {
                    members[secrets.Redact(name)] = RedactedNode(value, secrets);
                }

                return members;
            case JsonArray array:
                return new JsonArray([.. array.Select(element => RedactedNode(element, secrets))]);
            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(secrets.Redact(text));
            default:
                return node?.DeepClone();
        }
    }
}
