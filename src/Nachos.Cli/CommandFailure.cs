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
    /// The <c>--connection</c> value, for commands that have one. Any message can carry a host, database or user name from it, so
    /// every message has the string's sensitive tokens redacted (see <see cref="Redaction.ConnectionStringTokens"/>). Whether the
    /// string parses is the caller's check, made before anything runs; a failure here is never relabelled as a malformed string.
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

    private static string Describe(Exception failure, string? connectionString) =>
        connectionString is null
            ? failure.Message
            : Redaction.Scrub(failure.Message, Redaction.ConnectionStringTokens(connectionString));
}
