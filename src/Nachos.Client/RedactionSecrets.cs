using System.Text;

namespace Nachos.Client;

/// <summary>
/// The secrets of one call (the bearer value, and the API key) in every form a transport or server may echo them:
/// as plain text (matched exactly), and as the hex of their UTF-8 bytes, dash-separated (<c>65-79-4A</c>, as .NET
/// dumps an invalid chunk extension) or contiguous (<c>65794A</c>), the hex forms matched in any letter case.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Redact"/> replaces every match in one pass: overlapping or adjacent matches collapse into one
/// <see cref="ErrorMapper.Redacted"/>, and text that already is that marker is never matched again, so redacting twice
/// changes nothing and the marker cannot grow (a secret such as <c>redact</c> leaves <c>[redacted]</c> intact).
/// </para>
/// <para>
/// Matching is by substring, so an echo that holds the whole secret (or the whole hex of it, inside a longer dump) is
/// caught wherever it sits. Not caught: a partial echo, one split across lines, a different case of the plain text,
/// or any other encoding (percent-encoding, base64).
/// </para>
/// </remarks>
internal sealed class RedactionSecrets
{
    public static readonly RedactionSecrets None = new([]);

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
            forms.Add((secret, StringComparison.Ordinal));
            forms.Add((BitConverter.ToString(bytes), StringComparison.OrdinalIgnoreCase));
            forms.Add((Convert.ToHexString(bytes), StringComparison.OrdinalIgnoreCase));
        }

        return forms.Count == 0 ? None : new([.. forms.Distinct()]);
    }

    /// <summary>
    /// The bearer value of <paramref name="request"/>'s <c>Authorization</c> header, read without validation so a value
    /// that does not parse (an API key with <c>,</c> or <c>"</c> on a retry copy) is still found.
    /// </summary>
    public static RedactionSecrets FromAuthorization(HttpRequestMessage request) =>
        request.Headers.NonValidated.TryGetValues("Authorization", out var values)
            ? Of([.. values.Select(StripScheme)])
            : None;

    /// <summary>True when any form of any secret occurs in <paramref name="text"/> outside existing markers.</summary>
    public bool OccursIn(string text) =>
        !IsEmpty && text.Split(ErrorMapper.Redacted).Any(segment => _forms.Any(f => segment.Contains(f.Form, f.Comparison)));

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

    private static string StripScheme(string value) =>
        value.StartsWith(BearerScheme, StringComparison.OrdinalIgnoreCase) ? value[BearerScheme.Length..].Trim() : value.Trim();
}
