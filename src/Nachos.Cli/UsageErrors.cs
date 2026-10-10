using System.CommandLine;
using System.CommandLine.Parsing;
using Nachos.Abstractions.Domain;

namespace Nachos.Cli;

/// <summary>The validation errors the CLI raises itself. Each has fixed text that never contains a value from the command line.</summary>
internal enum UsageError
{
    AllowDataLossNeedsReview,
    ObjectIdEmpty,
    ObjectIdPadded,
    ObjectIdTooLong,
    RoleUnknown,
    WorkspaceInvalid,
    WorkspaceOnAdminRole,
}

/// <summary>
/// Raises <see cref="UsageError"/>s as parse errors. The parser's own messages quote what it was given, so <see cref="CliApp"/> redacts
/// any parser message that contains a command-line token; these are exempt, because a token that happens to occur in their fixed text
/// (the role "Nachos.Admin" in "--role must be one of: Nachos.Admin, ...") is not an echo. The exemption is exactly this set, so
/// there is no way to register an arbitrary message.
/// </summary>
internal static class UsageErrors
{
    internal static readonly IReadOnlyList<string> Messages = [.. Enum.GetValues<UsageError>().Select(Text)];

    private static readonly HashSet<string> Own = [.. Messages];

    /// <summary>Reports <paramref name="error"/> as a parse error of <paramref name="result"/>.</summary>
    public static void Add(CommandResult result, UsageError error) => result.AddError(Text(error));

    /// <summary>True when <paramref name="message"/> is the text of one of the <see cref="UsageError"/>s.</summary>
    public static bool IsOwn(string message) => Own.Contains(message);

    /// <summary>
    /// The value of <paramref name="option"/>, or its default when the option was given more than once. Reading a repeated
    /// single-value option throws, but the parser has already reported that as an error of its own (redacted as usual), so a
    /// validator has nothing to add and must not crash on it.
    /// </summary>
    public static T? SingleValue<T>(CommandResult result, Option<T> option)
    {
        try
        {
            return result.GetValue(option);
        }
        catch (InvalidOperationException)
        {
            return default;
        }
    }

    /// <summary>
    /// Whether a flag was given. A flag that is repeated without values is accepted by the parser, but reading its value in a validator
    /// throws; being given at all is what matters for a rule such as "--allow-data-loss needs --approve-reviewed".
    /// </summary>
    public static bool IsSet(CommandResult result, Option<bool> flag)
    {
        try
        {
            return result.GetValue(flag);
        }
        catch (InvalidOperationException)
        {
            return result.GetResult(flag) is not null;
        }
    }

    private static string Text(UsageError error) => error switch
    {
        UsageError.AllowDataLossNeedsReview => "--allow-data-loss is only valid together with --approve-reviewed.",
        UsageError.ObjectIdEmpty => "--object-id must not be empty.",
        UsageError.ObjectIdPadded => "--object-id must not start or end with whitespace.",
        UsageError.ObjectIdTooLong => $"--object-id must be at most {GrantCommands.MaxObjectIdLength} characters.",
        UsageError.RoleUnknown => $"--role must be one of: {string.Join(", ", GrantCommands.KnownRoles)}.",
        UsageError.WorkspaceInvalid => "--workspace must be 1–512 ASCII letters, digits, '_' or '-'.",
        UsageError.WorkspaceOnAdminRole => $"--workspace applies only to the {GrantRoles.Workspace} role; {GrantRoles.Admin} is not scoped to a workspace.",
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };
}
