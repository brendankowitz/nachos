namespace Nachos.Cli;

/// <summary>Runs a command body and turns anything it throws into an exit code, so no stack trace or secret reaches the terminal.</summary>
internal static class CommandFailure
{
    /// <summary>
    /// Runs <paramref name="body"/>; any exception becomes exit 1 with one line on <paramref name="error"/>.
    /// </summary>
    /// <param name="error">Where the message goes.</param>
    /// <param name="body">The command.</param>
    /// <param name="connectionString">
    /// The <c>--connection</c> value, for commands that have one. SqlClient quotes the pieces of a string it cannot parse, and any
    /// message can carry a host or database name, so such a failure is replaced by a fixed message and every other message has the
    /// string's tokens redacted.
    /// </param>
    public static async Task<int> GuardAsync(TextWriter error, Func<Task<int>> body, string? connectionString = null)
    {
        try
        {
            return await body();
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Canceled.");
            return ExitCodes.Error;
        }
        catch (Exception failure)
        {
            await error.WriteLineAsync($"error: {Describe(failure, connectionString)}");
            return ExitCodes.Error;
        }
    }

    private static string Describe(Exception failure, string? connectionString)
    {
        if (connectionString is null)
        {
            return failure.Message;
        }

        // The connection string is the only free-form input of the schema commands, so an argument or format error is about it.
        for (var cause = failure; cause is not null; cause = cause.InnerException)
        {
            if (cause is ArgumentException or FormatException)
            {
                return "The connection string is malformed (details redacted).";
            }
        }

        return Redaction.Scrub(failure.Message, Redaction.ConnectionStringTokens(connectionString));
    }
}
