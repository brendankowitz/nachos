namespace Nachos.Abstractions.Schema;

/// <summary>Who has vouched for a schema change.</summary>
public enum DeployApproval
{
    /// <summary>Nobody: only changes classified <see cref="DeployClassification.AutoSafe"/> are applied. Used by the schema gate and as the CLI default.</summary>
    AutoSafeOnly,

    /// <summary>
    /// An operator has read the report, so <see cref="DeployClassification.Unsafe"/> and
    /// <see cref="DeployClassification.Unclassifiable"/> changes are applied too. Data loss is still blocked
    /// unless it is allowed separately.
    /// </summary>
    OperatorReviewed,
}