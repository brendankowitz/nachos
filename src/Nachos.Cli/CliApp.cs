using System.CommandLine;

namespace Nachos.Cli;

/// <summary>The <c>nachos</c> bootstrap command line: schema management and offline key minting.</summary>
public static class CliApp
{
    /// <summary>
    /// Runs the command line with the given streams and returns the process exit code (see <see cref="ExitCodes"/>).
    /// Usage errors go to <paramref name="error"/> only, so <paramref name="output"/> carries a command's result and nothing else.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var root = new RootCommand("Nachos bootstrap tool: database schema management and offline key minting.")
        {
            SchemaCommands.Create(),
            KeyCommands.Create(),
        };

        var parsed = root.Parse(args);
        if (parsed.Errors.Count > 0)
        {
            foreach (var parseError in parsed.Errors)
            {
                await error.WriteLineAsync(parseError.Message);
            }

            await error.WriteLineAsync("Run with --help for usage.");
            return ExitCodes.Error;
        }

        return await parsed.InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, ct);
    }
}
