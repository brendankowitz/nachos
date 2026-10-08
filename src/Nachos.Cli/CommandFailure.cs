namespace Nachos.Cli;

/// <summary>Runs a command body and turns anything it throws into an exit code, so no stack trace or token reaches the terminal.</summary>
internal static class CommandFailure
{
    public static async Task<int> GuardAsync(TextWriter error, Func<Task<int>> body)
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
            await error.WriteLineAsync($"error: {failure.Message}");
            return ExitCodes.Error;
        }
    }
}
