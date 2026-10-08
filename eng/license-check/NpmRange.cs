using System.Globalization;
using System.Text.RegularExpressions;

namespace Nachos.LicenseCheck;

// Only registry semver ranges are supported, never URLs, tags or aliases.
internal static class NpmRange
{
    private readonly record struct Number(int Major, int Minor, int Patch, string Pre) : IComparable<Number>
    {
        public int CompareTo(Number other)
        {
            var order = Major.CompareTo(other.Major);
            if (order == 0) order = Minor.CompareTo(other.Minor);
            if (order == 0) order = Patch.CompareTo(other.Patch);
            if (order != 0) return order;
            if (Pre == other.Pre) return 0;
            if (Pre.Length == 0) return 1;
            if (other.Pre.Length == 0) return -1;
            var left = Pre.Split('.');
            var right = other.Pre.Split('.');
            for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
            {
                var a = left[i];
                var b = right[i];
                var numericA = a.All(char.IsAsciiDigit);
                var numericB = b.All(char.IsAsciiDigit);
                order = numericA && numericB ? a.Length.CompareTo(b.Length)
                    : numericA != numericB ? (numericA ? -1 : 1) : 0;
                if (order == 0) order = string.CompareOrdinal(a, b);
                if (order != 0) return order;
            }
            return left.Length.CompareTo(right.Length);
        }
    }

    public static bool Matches(string version, string range)
    {
        var candidate = Parse(version, out var candidateParts);
        if (candidateParts != 3) throw new InvalidDataException($"Non-exact npm version: {version}");
        var alternatives = range.Split("||", StringSplitOptions.TrimEntries);
        var matched = false;
        foreach (var alternative in alternatives)
        {
            if (string.IsNullOrWhiteSpace(alternative)) throw new InvalidDataException($"Unsupported npm range: {range}");
            var hyphen = Regex.Match(alternative, @"^(\S+)\s+-\s+(\S+)$");
            var terms = hyphen.Success ? new[] { ">=" + hyphen.Groups[1].Value, "<=" + hyphen.Groups[2].Value }
                : Regex.Replace(alternative, @"([<>]=?|[=~^])\s+", "$1").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var accepts = true;
            var prerelease = candidate.Pre.Length == 0;
            foreach (var term in terms)
            {
                var match = Regex.Match(term, @"^(>=|<=|>|<|=|~|\^)?(.+)$");
                var op = match.Groups[1].Value;
                var bound = Parse(match.Groups[2].Value, out var parts);
                if (bound.Pre.Length > 0 && candidate.Major == bound.Major && candidate.Minor == bound.Minor && candidate.Patch == bound.Patch)
                    prerelease = true;
                var next = parts <= 1 ? new Number(checked(bound.Major + 1), 0, 0, "")
                    : new Number(bound.Major, checked(bound.Minor + 1), 0, "");
                var comparison = candidate.CompareTo(bound);
                var termAccepts = op switch
                {
                    "" or "=" => parts == 0 || comparison >= 0 && (parts == 3 ? comparison == 0 : candidate.CompareTo(next) < 0),
                    ">=" => parts == 0 || comparison >= 0,
                    ">" => parts != 0 && (parts == 3 ? comparison > 0 : candidate.CompareTo(next) >= 0),
                    "<" => parts != 0 && comparison < 0,
                    "<=" => parts == 0 || (parts == 3 ? comparison <= 0 : candidate.CompareTo(next) < 0),
                    "~" => parts == 0 || comparison >= 0 && candidate.CompareTo(next) < 0,
                    "^" => parts == 0 || comparison >= 0 && candidate.CompareTo(
                        bound.Major > 0 || parts == 1 ? new Number(checked(bound.Major + 1), 0, 0, "")
                        : bound.Minor > 0 || parts == 2 ? new Number(0, checked(bound.Minor + 1), 0, "")
                        : new Number(0, 0, checked(bound.Patch + 1), "")) < 0,
                    _ => throw new InvalidDataException($"Unsupported npm range: {range}")
                };
                accepts &= termAccepts;
            }
            matched |= accepts && prerelease;
        }
        return matched;
    }

    private static Number Parse(string text, out int parts)
    {
        var match = Regex.Match(text, @"^(0|[1-9]\d*|[xX*])(?:\.(0|[1-9]\d*|[xX*]))?(?:\.(0|[1-9]\d*|[xX*]))?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$");
        if (!match.Success) throw new InvalidDataException($"Unsupported npm semver/range token: {text}");
        var numbers = new int[3];
        parts = 0;
        for (var i = 1; i <= 3; i++)
        {
            var value = match.Groups[i].Value;
            if (value.Length == 0 || value is "x" or "X" or "*")
            {
                if (Enumerable.Range(i + 1, 3 - i).Any(index => match.Groups[index].Success && match.Groups[index].Value is not ("x" or "X" or "*")))
                    throw new InvalidDataException($"Unsupported npm wildcard: {text}");
                break;
            }
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i - 1]) || numbers[i - 1] == int.MaxValue)
                throw new InvalidDataException($"Unsupported npm version number: {text}");
            parts++;
        }
        var pre = match.Groups[4].Value;
        if (pre.Length > 0 && (parts != 3 || pre.Split('.').Any(item => item.Length > 1 && item[0] == '0' && item.All(char.IsAsciiDigit))))
            throw new InvalidDataException($"Invalid npm prerelease: {text}");
        return new Number(numbers[0], numbers[1], numbers[2], pre);
    }
}
