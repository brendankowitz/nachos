namespace Nachos.Cli;

/// <summary>Process exit codes. The azd hooks and operators rely on telling a refusal from a failure.</summary>
internal static class ExitCodes
{
    /// <summary>The command did what was asked, or there was nothing to do.</summary>
    public const int Success = 0;

    /// <summary>A usage error, a connection failure, or an unexpected exception.</summary>
    public const int Error = 1;

    /// <summary>The request was understood but Nachos refuses to apply it without further review.</summary>
    public const int Refused = 2;
}
