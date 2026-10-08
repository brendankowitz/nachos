using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.RegularExpressions;

namespace Nachos.Cli;

/// <summary>The <c>nachos</c> bootstrap command line: schema management and offline key minting.</summary>
public static class CliApp
{
    /// <summary>
    /// Runs the command line with the given streams and returns the process exit code (see <see cref="ExitCodes"/>).
    /// A command's result goes to <paramref name="output"/>, and so does help that was asked for explicitly
    /// (<c>--help</c>, <c>--version</c>; exit 0). Every error goes to <paramref name="error"/> only, redacted so that a secret
    /// passed on the command line is never echoed.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var root = new RootCommand("Nachos bootstrap tool: database schema management and offline key minting.")
        {
            SchemaCommands.Create(),
            KeyCommands.Create(),
        };

        // A leading '@' is data here (a secret may start with one), not a response file whose path would be echoed when missing.
        var parsed = root.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null });
        if (parsed.Errors.Count > 0)
        {
            var userTokens = UserTokens(root, args, parsed);
            // Redaction makes several errors about one stray token read the same; say it once.
            foreach (var message in parsed.Errors.Select(parseError => Redacted(parseError, userTokens)).Distinct())
            {
                await error.WriteLineAsync(message);
            }

            await error.WriteLineAsync("Run with --help for usage.");
            return ExitCodes.Error;
        }

        return await parsed.InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, ct);
    }

    // Everything on the command line that is not the name of a command or option: values and stray words, any of which may be a secret.
    // Both the raw arguments and the parser's own tokens are used: '--opt=value' and '--opt:value' are one argument that the parser
    // splits, and either half may be echoed.
    private static HashSet<string> UserTokens(Command root, string[] args, ParseResult parsed)
    {
        var symbolNames = new HashSet<string>(StringComparer.Ordinal);
        void Collect(Command command)
        {
            symbolNames.Add(command.Name);
            symbolNames.UnionWith(command.Aliases);
            foreach (var option in command.Options)
            {
                symbolNames.Add(option.Name);
                symbolNames.UnionWith(option.Aliases);
            }

            foreach (var child in command.Subcommands)
            {
                Collect(child);
            }
        }

        Collect(root);

        var candidates = new List<string>();
        foreach (var arg in args)
        {
            candidates.Add(arg);
            var split = arg.IndexOfAny(['=', ':']);
            if (split >= 0)
            {
                candidates.Add(arg[..split]);
                candidates.Add(arg[(split + 1)..]);
            }
        }

        candidates.AddRange(parsed.Tokens.Select(token => token.Value));
        return [.. candidates.Where(token => token.Length > 0 && !symbolNames.Contains(token))];
    }

    // The parser quotes the offending value in its messages, so a message that contains a user token, quoted or as a whole word, is
    // replaced by one that names only the symbol it is about. A token that merely occurs inside another word does not count: "a"
    // must not turn "Required argument missing" into a redaction.
    private static string Redacted(ParseError error, HashSet<string> userTokens)
    {
        if (!userTokens.Any(token => Echoes(error.Message, token)))
        {
            return error.Message;
        }

        return error.SymbolResult switch
        {
            OptionResult option => $"Invalid value for option {option.Option.Name} (value redacted).",
            ArgumentResult => "Invalid argument (value redacted).",
            _ => "Unrecognized argument (value redacted).",
        };
    }

    private static bool Echoes(string message, string token) =>
        message.Contains($"'{token}'", StringComparison.Ordinal) ||
        Regex.IsMatch(message, $@"(?<![\w-]){Regex.Escape(token)}(?![\w-])", RegexOptions.CultureInvariant);
}
