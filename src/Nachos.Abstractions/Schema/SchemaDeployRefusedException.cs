namespace Nachos.Abstractions.Schema;

/// <summary>
/// A deploy was declined by policy before anything was changed. It is not a failure: nothing was applied, and what the
/// operator must do differs by <see cref="Reason"/>. Genuine failures (SQL errors, timeouts, DacFx errors) are never reported
/// this way, so callers can tell the two apart by type.
/// </summary>
public sealed class SchemaDeployRefusedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reason">Why the deploy was declined.</param>
    /// <param name="message">A one-sentence description of the refusal.</param>
    /// <param name="reasons">The specific findings behind it, for example each database option that differs.</param>
    /// <param name="possibleDataLoss">The change the deploy would make could lose data.</param>
    public SchemaDeployRefusedException(
        SchemaRefusalReason reason, string message, IReadOnlyList<string>? reasons = null, bool possibleDataLoss = false)
        : base(message)
    {
        Reason = reason;
        Reasons = reasons ?? [];
        PossibleDataLoss = possibleDataLoss;
    }

    /// <summary>Why the deploy was declined.</summary>
    public SchemaRefusalReason Reason { get; }

    /// <summary>The specific findings behind the refusal. Empty when <see cref="Exception.Message"/> says it all.</summary>
    public IReadOnlyList<string> Reasons { get; }

    /// <summary>
    /// True when the change that was refused could lose data, so approving it also needs data loss to be allowed.
    /// Always true for <see cref="SchemaRefusalReason.DataLossBlocked"/>; it also tells an operator who is refused for another
    /// reason that review alone will not be enough.
    /// </summary>
    public bool PossibleDataLoss { get; }
}
