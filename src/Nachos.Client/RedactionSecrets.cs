using System.Text;

namespace Nachos.Client;

/// <summary>
/// The bearer value of one call, as plain text (matched in any letter case from <see cref="MinIgnoreCaseLength"/>
/// characters, exactly below that) and as the hex of its UTF-8 bytes, dash-separated (<c>65-79-4A</c>) or contiguous
/// (<c>65794A</c>), the hex forms matched in any letter case and only when they are at least
/// <see cref="MinHexLength"/> characters long.
/// </summary>
/// <remarks>
/// <para>
/// This is the second layer. It redacts server text the client shows on purpose (mapped error bodies, reason phrases;
/// see <see cref="ErrorMapper"/>) and the few transport failures <see cref="SecretRedaction"/> keeps. It does not
/// protect the malformed-response family: those failures are replaced by fixed text, because no list of encodings can
/// cover every way a transport describes reflected bytes.
/// </para>
/// <para>
/// <see cref="Redact"/> replaces every match in one pass: overlapping or adjacent matches collapse into one
/// <see cref="ErrorMapper.Redacted"/>, and text that already is that marker is never matched again, so redacting twice
/// changes nothing and the marker cannot grow (a secret such as <c>redact</c> leaves <c>[redacted]</c> intact).
/// </para>
/// <para>
/// Matching is by substring, so an echo that holds the whole secret (or the whole hex of it) is caught wherever it sits,
/// in any letter case unless the secret is very short. Not caught: a partial echo, one split across lines, another
/// case of a secret shorter than <see cref="MinIgnoreCaseLength"/>, or another encoding.
/// </para>
/// </remarks>
internal sealed class RedactionSecrets
{
    public static readonly RedactionSecrets None = new([]);

    /// <summary>
    /// Shortest hex form that is matched. Hex of a very short secret (a key of "0" is "30") would match ordinary digits
    /// such as a status code and corrupt unrelated text; plain matching has no minimum.
    /// </summary>
    public const int MinHexLength = 8;

    /// <summary>
    /// Shortest plain secret that is matched in any letter case: a server echoing the value lower-cased in a mapped
    /// <c>detail</c> is caught. A very short secret (a key of <c>ab</c>) matched that way would also hit ordinary words
    /// in server text and garble them, so below this length the plain form is matched exactly. The same length as
    /// <see cref="MinHexLength"/>, for the same reason.
    /// </summary>
    public const int MinIgnoreCaseLength = MinHexLength;

    /// <summary>
    /// The run of characters a response header <em>name</em> must share with the bearer value to count as an echo of
    /// it (<see cref="HeaderNameEchoes"/>), and so the shortest bearer value that is matched against names at all. A
    /// name echoing the value is removed before anything above the primary handler sees it, so a short value
    /// that is a substring of an ordinary name (a key of <c>e</c>, <c>ry</c> or <c>After</c> is inside
    /// <c>Retry-After</c>) would strip real headers and change retry behaviour. Real keys are JWTs, hundreds of
    /// characters long, so values shorter than this are never matched against names; mapped text keeps plain matching
    /// at any length.
    /// </summary>
    public const int MinHeaderNameMatchLength = 16;

    private const string BearerScheme = "Bearer ";

    private readonly (string Form, StringComparison Comparison)[] _forms;

    private RedactionSecrets((string Form, StringComparison Comparison)[] forms)
    {
        _forms = forms;
    }

    public bool IsEmpty => _forms.Length == 0;

    /// <summary>The secrets among <paramref name="values"/> (nulls and empty strings ignored), in all their forms.</summary>
    public static RedactionSecrets Of(params string?[] values)
    {
        var forms = new List<(string, StringComparison)>();
        foreach (var secret in values.OfType<string>().Where(v => v.Length > 0).Distinct(StringComparer.Ordinal))
        {
            var bytes = Encoding.UTF8.GetBytes(secret);
            forms.Add((secret, secret.Length >= MinIgnoreCaseLength ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            foreach (var hex in new[] { BitConverter.ToString(bytes), Convert.ToHexString(bytes) })
            {
                if (hex.Length >= MinHexLength)
                {
                    forms.Add((hex, StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        return forms.Count == 0 ? None : new([.. forms.Distinct()]);
    }

    /// <summary>The bearer value of <paramref name="request"/>'s <c>Authorization</c> header (see <see cref="Bearer"/>), in all its forms.</summary>
    public static RedactionSecrets FromAuthorization(HttpRequestMessage request) => Of(Bearer(request));

    /// <summary>
    /// The bearer value of <paramref name="request"/> that response header names are checked against
    /// (<see cref="HeaderNameEchoes"/>): its <c>Authorization</c> value when that is at least
    /// <see cref="MinHeaderNameMatchLength"/> long, else null.
    /// </summary>
    public static string? BearerForHeaderNames(HttpRequestMessage request) =>
        Bearer(request) is { Length: >= MinHeaderNameMatchLength } bearer ? bearer : null;

    /// <summary>
    /// True when <paramref name="name"/>, a response header name, shares a run of <see cref="MinHeaderNameMatchLength"/>
    /// characters with <paramref name="bearer"/>, in any letter case: the whole value, a part of it (the signature
    /// segment, the value minus its first character) or another case of it all count, so a name is dropped unless it
    /// is clearly unrelated. A bearer shorter than the run is never echoed (see the constant). Allocates nothing; the
    /// cost is one search of the value per window of the name, and a response's names are bounded by the handler's
    /// header size limit. Hex forms of the value are not runs of it; <see cref="OccursIn"/> finds those whole.
    /// </summary>
    public static bool HeaderNameEchoes(string name, string bearer)
    {
        if (bearer.Length < MinHeaderNameMatchLength)
        {
            return false;
        }

        var value = bearer.AsSpan();
        for (var at = 0; at + MinHeaderNameMatchLength <= name.Length; at++)
        {
            if (value.Contains(name.AsSpan(at, MinHeaderNameMatchLength), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when any form of any secret occurs in <paramref name="text"/> outside existing markers.</summary>
    public bool OccursIn(string text) =>
        !IsEmpty && text.Split(ErrorMapper.Redacted).Any(segment => _forms.Any(f => segment.Contains(f.Form, f.Comparison)));

    /// <summary>
    /// The bearer value of <paramref name="request"/>'s <c>Authorization</c> header, or null. The parsed header is read
    /// first: its parameter is the string the client set, so a request built by <see cref="NachosHttpClient"/>
    /// allocates nothing here. A retry copy stores the raw text (<see cref="RetryHandler"/> copies headers unvalidated),
    /// which the parsed read parses once, the cost of its length; a value that does not parse (an API key with
    /// <c>,</c> or <c>"</c>) is then read unvalidated, so it is still found.
    /// </summary>
    private static string? Bearer(HttpRequestMessage request)
    {
        if (request.Headers.Authorization?.Parameter is { } parameter)
        {
            return parameter;
        }

        return request.Headers.NonValidated.TryGetValues("Authorization", out var values)
            ? StripScheme(values.ToString())
            : null;
    }

    /// <summary><paramref name="text"/> with every match replaced, in one pass (see the type remarks). Not bounded.</summary>
    public string Redact(string text)
    {
        if (IsEmpty || text.Length == 0)
        {
            return text;
        }

        var segments = text.Split(ErrorMapper.Redacted);
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = RedactSegment(segments[i]);
        }

        return string.Join(ErrorMapper.Redacted, segments);
    }

    private string RedactSegment(string segment)
    {
        bool[]? hidden = null;
        foreach (var (form, comparison) in _forms)
        {
            for (var at = segment.IndexOf(form, comparison); at >= 0; at = segment.IndexOf(form, at + 1, comparison))
            {
                hidden ??= new bool[segment.Length];
                Array.Fill(hidden, true, at, form.Length);
            }
        }

        if (hidden is null)
        {
            return segment;
        }

        var result = new StringBuilder(segment.Length);
        for (var i = 0; i < segment.Length; i++)
        {
            if (!hidden[i])
            {
                result.Append(segment[i]);
            }
            else if (i == 0 || !hidden[i - 1])
            {
                result.Append(ErrorMapper.Redacted);
            }
        }

        return result.ToString();
    }

    private static string StripScheme(string value) => StripScheme(value.AsSpan()).ToString();

    private static ReadOnlySpan<char> StripScheme(ReadOnlySpan<char> value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith(BearerScheme, StringComparison.OrdinalIgnoreCase) ? trimmed[BearerScheme.Length..].Trim() : trimmed;
    }
}
