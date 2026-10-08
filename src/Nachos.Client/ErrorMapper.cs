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

    public static Exception Map(HttpResponseMessage response, string body, string operation, string? secret)
    {
        var status = response.StatusCode;
        var (detail, parsed, type) = Parse(body);
        var fallback = string.Create(
            CultureInfo.InvariantCulture, $"Nachos {operation} returned {(int)status} {response.ReasonPhrase}.");
        var message = Sanitize(detail ?? fallback, secret);

        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new AuthException(message),
            HttpStatusCode.NotFound => new NotFoundException(message),
            HttpStatusCode.Conflict => new ConflictException(message),
            HttpStatusCode.UnprocessableEntity when parsed is { } errors => new RequestValidationException(Sanitize(errors, secret)),
            HttpStatusCode.UnprocessableEntity when IsIdempotencyKeyReused(type) => new IdempotencyKeyReusedException(message),
            HttpStatusCode.UnprocessableEntity => new NachosValidationException(message),
            _ => new HttpRequestException(
                Sanitize(detail is null ? fallback : $"{fallback} {detail}", secret), inner: null, status),
        };
    }

    private static string Sanitize(string text, string? secret)
    {
        var redacted = string.IsNullOrEmpty(secret) ? text : text.Replace(secret, Redacted, StringComparison.Ordinal);
        if (redacted.Length <= MaxMessageLength)
        {
            return redacted;
        }

        var cut = MaxMessageLength - TruncationMarker.Length;
        if (char.IsHighSurrogate(redacted[cut - 1]))
        {
            cut--; // never keep half of a surrogate pair
        }

        return string.Concat(redacted.AsSpan(0, cut), TruncationMarker);
    }

    // Every server-supplied string (msg, type, string loc parts) is redacted and bounded like the detail text.
    private static ValidationError[] Sanitize((ValidationError[] Kept, int Omitted) errors, string? secret)
    {
        var sanitized = errors.Kept
            .Select(e => new ValidationError(
                [.. e.Loc.Select(part => part is string text ? Sanitize(text, secret) : part)],
                Sanitize(e.Msg, secret),
                Sanitize(e.Type, secret)))
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

    // FastAPI loc entries are member names (strings) or array indexes (integers).
    private static object LocPart(JsonNode? part) => part switch
    {
        JsonValue v when v.TryGetValue<string>(out var name) => name,
        JsonValue v when v.TryGetValue<int>(out var index) => index,
        _ => part?.ToJsonString() ?? "null",
    };
}
