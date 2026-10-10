using System.Text.RegularExpressions;

namespace Nachos.LicenseCheck;

internal sealed record Spdx(List<HashSet<string>> Alternatives, bool HasChoice)
{
    public HashSet<string> AllLicenses => Alternatives.SelectMany(branch => branch).ToHashSet(StringComparer.Ordinal);

    public static Spdx Parse(string input)
    {
        var tokens = Regex.Matches(input, @"\(|\)|[A-Za-z0-9][A-Za-z0-9.+-]*|\S").Select(match => match.Value).ToArray();
        if (tokens.Length is 0 or > 128)
        {
            throw new InvalidDataException($"Malformed or oversized SPDX expression: {input}");
        }
        var index = 0;
        var choice = false;
        List<HashSet<string>> Atom()
        {
            if (index >= tokens.Length)
            {
                throw new InvalidDataException($"Malformed SPDX expression: {input}");
            }
            var token = tokens[index++];
            if (token == "(")
            {
                var nested = Or();
                if (index >= tokens.Length || tokens[index++] != ")")
                {
                    throw new InvalidDataException($"Unbalanced SPDX expression: {input}");
                }
                return nested;
            }
            if (token is "AND" or "OR" or "WITH" || !Regex.IsMatch(token, @"^[A-Za-z0-9][A-Za-z0-9.+-]*$"))
            {
                throw new InvalidDataException($"Malformed SPDX token: {token}");
            }
            return [new HashSet<string>([token], StringComparer.Ordinal)];
        }
        List<HashSet<string>> And()
        {
            var result = Atom();
            while (index < tokens.Length && tokens[index] == "AND")
            {
                index++;
                var right = Atom();
                if (result.Count * right.Count > 64)
                {
                    throw new InvalidDataException("SPDX expression has too many branches.");
                }
                result = result.SelectMany(left => right.Select(branch => left.Concat(branch).ToHashSet(StringComparer.Ordinal))).ToList();
            }
            return result;
        }
        List<HashSet<string>> Or()
        {
            var result = And();
            while (index < tokens.Length && tokens[index] == "OR")
            {
                choice = true;
                index++;
                result.AddRange(And());
                if (result.Count > 64)
                {
                    throw new InvalidDataException("SPDX expression has too many branches.");
                }
            }
            return result;
        }
        var alternatives = Or();
        if (index != tokens.Length)
        {
            throw new InvalidDataException($"Malformed or unsupported SPDX expression: {input}");
        }
        return new Spdx(alternatives, choice);
    }
}
