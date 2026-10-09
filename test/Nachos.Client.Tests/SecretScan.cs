using System.Text;
using System.Text.RegularExpressions;

namespace Nachos.Client.Tests;

/// <summary>
/// Looks for any <c>window</c>-character substring of a secret in a text: as plain text, and inside every hex run of
/// the text decoded to bytes (dash-separated pairs, and contiguous digits at both alignments, any letter case). So a
/// partial echo, a tail, or the hex dump of any part of the secret is found, not only the whole value.
/// </summary>
internal static partial class SecretScan
{
    /// <returns>A description of the first leak found, or null.</returns>
    public static string? FindLeak(string text, string secret, int window)
    {
        var windows = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + window <= secret.Length; i++)
        {
            windows.Add(secret.Substring(i, window));
        }

        var lookup = windows.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var (form, candidate) in Candidates(text))
        {
            for (var i = 0; i + window <= candidate.Length; i++)
            {
                if (lookup.Contains(candidate.AsSpan(i, window)))
                {
                    return $"a {window}-character piece of the secret, {form}, at {i}";
                }
            }
        }

        return null;
    }

    private static IEnumerable<(string Form, string Text)> Candidates(string text)
    {
        yield return ("as plain text", text);
        foreach (Match run in DashHex().Matches(text))
        {
            yield return ("as dash-separated hex", Decode(run.Value.Replace("-", string.Empty, StringComparison.Ordinal)));
        }

        foreach (Match run in ContiguousHex().Matches(text))
        {
            yield return ("as contiguous hex", Decode(run.Value));
            yield return ("as contiguous hex (odd alignment)", Decode(run.Value[1..]));
        }
    }

    private static string Decode(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return Encoding.Latin1.GetString(bytes);
    }

    [GeneratedRegex("[0-9A-Fa-f]{2}(?:-[0-9A-Fa-f]{2})+")]
    private static partial Regex DashHex();

    [GeneratedRegex("[0-9A-Fa-f]{4,}")]
    private static partial Regex ContiguousHex();
}
