using System.Collections.Concurrent;
using System.CommandLine.Parsing;

namespace Nachos.Cli;

/// <summary>
/// Validation errors the CLI raises itself. The parser's own messages quote what it was given, so <see cref="CliApp"/> redacts any
/// parser message that contains a command-line token; these messages are built only from fixed text, never from a value, so a
/// token that happens to occur in one (the role "Nachos.Admin" in "--role must be one of: Nachos.Admin, ...") is not an echo.
/// </summary>
internal static class UsageErrors
{
    private static readonly ConcurrentDictionary<string, byte> Own = new(StringComparer.Ordinal);

    /// <summary>Reports <paramref name="message"/> as a parse error. It must not contain anything taken from the command line.</summary>
    public static void Add(CommandResult result, string message)
    {
        Own.TryAdd(message, 0);
        result.AddError(message);
    }

    /// <summary>True when <paramref name="message"/> was raised through <see cref="Add"/>.</summary>
    public static bool IsOwn(string message) => Own.ContainsKey(message);
}
