using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nachos.LicenseCheck;

internal static class LicenseText
{
    private const string NoticeHeading = @"(?im)^[\t ]*(?:#+[\t ]*)?(?:third[- ]party|bundled)[\t -]+(?:notices?|licen[cs]es?|components)[\t ]*:?\r?$";
    private static readonly (string License, string Text)[] Templates = LoadTemplates();

    public static (HashSet<string> Licenses, bool Prohibited) Identify(string text)
    {
        var remaining = Normalize(text);
        var licenses = new HashSet<string>(StringComparer.Ordinal);
        if (Regex.IsMatch(remaining, "software license terms|licensed, not sold|proprietary license"))
        {
            licenses.Add("LicenseRef-Proprietary");
        }
        while (remaining.Length > 0)
        {
            var match = Templates.FirstOrDefault(template => remaining.StartsWith(template.Text, StringComparison.Ordinal));
            if (match.Text is null)
            {
                // A recognized grant followed by unexplained terms is not that grant.
                // In particular, a review cannot silently drop those extra obligations.
                if (licenses.Count > 0)
                {
                    licenses.Add("LicenseRef-Unrecognized-Text");
                }
                return (licenses, Prohibited(remaining));
            }
            licenses.Add(match.License);
            remaining = remaining[match.Text.Length..].TrimStart();
        }
        // Only complete canonical EPL/MPL/Python documents may contain their known,
        // non-granting secondary-license definitions/history without a false GPL alarm.
        return (licenses, false);
    }

    private static (string License, string Text)[] LoadTemplates()
    {
        using var stream = typeof(LicenseText).Assembly.GetManifestResourceStream("Nachos.LicenseCheck.license-templates.json")
            ?? throw new InvalidOperationException("Canonical license templates missing.");
        var texts = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        var result = new List<(string License, string Text)>();
        foreach (var (id, text) in texts)
        {
            result.Add((id, Normalize(text)));
            // These titles are optional labels; no operative paragraph is optional.
            if (id is "MIT" or "ISC")
            {
                result.Add((id, Normalize(Regex.Replace(text, @"\A" + id + @" License\s*", ""))));
            }
        }
        return result.OrderByDescending(template => template.Text.Length).ToArray();
    }

    private static string Normalize(string text)
    {
        text = text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('“', '"').Replace('”', '"').Replace('‘', '\'').Replace('’', '\'');
        text = Regex.Replace(text, NoticeHeading, "");
        text = Regex.Replace(text, @"(?m)^\s*#{1,6} ", "");
        text = Regex.Replace(text, @"(?m)^[\t ]*[-_=]{3,}[\t ]*$", "");
        text = Regex.Replace(text, @"(?m)^This license is GPL-compatible\.[\t ]*$", "");
        // Only years vary mechanically. Deleting an arbitrary holder field can
        // also delete restrictions; unknown attribution variants require review.
        text = Regex.Replace(text,
            @"(?im)^([\t ]*copyright[\t ]*(?:\(c\)|©)?[\t ]*)(?:\d{4}(?:[-, ]+\d{4})*|<year>|\[year\]|\[yyyy\])(?=[\t ])",
            "$1<year>");
        return Regex.Replace(text, @"\s+", " ").Trim().ToLowerInvariant();
    }

    public static bool Prohibited(string text) => Regex.IsMatch(text,
        @"\b(?:A?GPL|LGPL|SSPL)(?:[-\s\d.]|v?\d|$)|GNU\s+(?:(?:AFFERO|LESSER|LIBRARY)\s+)?GENERAL\s+PUBLIC\s+LICENSE|SERVER\s+SIDE\s+PUBLIC\s+LICENSE",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsLicensePath(string path)
    {
        var name = path.Replace('\\', '/').Split('/')[^1];
        return Regex.IsMatch(name, @"^(?:.*[ ._-])?(?:licen[cs]es?|copying|copyright|notices?|unlicense)(?:$|[ ._-])|^thirdpartynotices",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool IsNoticePath(string path)
    {
        var name = path.Replace('\\', '/').Split('/')[^1];
        return Regex.IsMatch(name, @"(?:^|[ ._-])notices?(?:$|[ ._-])|third.?party",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool IsNoticeDocument(string text)
    {
        var heading = Regex.Match(text, NoticeHeading);
        return heading.Success && string.IsNullOrWhiteSpace(text[..heading.Index]);
    }

    public static bool IsDocumentationPath(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is "" or ".txt" or ".md" or ".rst" or ".markdown" or ".html" or ".license" or ".apache" or ".mit" or ".bsd"
            || Regex.IsMatch(extension, @"^\.\d+$");
    }

    public static bool IsImplicitNpmDocument(string path)
    {
        if (!IsLicensePath(path) && !path.StartsWith("licenses/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("licences/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (Path.GetExtension(path).ToLowerInvariant() is ".js" or ".mjs" or ".cjs" or ".ts" or ".tsx" or ".jsx"
            or ".py" or ".cs" or ".fs" or ".go" or ".rb" or ".rs" or ".c" or ".h" or ".cc" or ".cpp" or ".java"
            or ".sh" or ".ps1" or ".bat" or ".cmd")
        {
            return false;
        }
        if (!IsDocumentationPath(path))
        {
            throw new InvalidDataException($"unsupported license entry (not a supported documentation file): {path}");
        }
        return true;
    }
}
