using System.CommandLine;
using System.CommandLine.Parsing;

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
            var userTokens = UserTokens(root, args);
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
    private static HashSet<string> UserTokens(Command root, string[] args)
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
        return [.. args.Where(arg => arg.Length > 0 && !symbolNames.Contains(arg))];
    }

    // The parser quotes the offending value in its messages, so a message that contains any user token is replaced by one that
    // names only the symbol it is about.
    private static string Redacted(ParseError error, HashSet<string> userTokens)
    {
        if (!userTokens.Any(token => error.Message.Contains(token, StringComparison.Ordinal)))
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
}
