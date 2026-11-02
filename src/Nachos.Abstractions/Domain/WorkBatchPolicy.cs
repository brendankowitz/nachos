namespace Nachos.Abstractions.Domain;

/// <summary>
/// When a unit of one task type is due, and how much of it one lease takes. A unit is due when its claimable items'
/// summed <c>TokenCount</c> reaches <see cref="EligibilityTokens"/>, or its oldest claimable item was enqueued at least
/// <see cref="MaxAge"/> ago. A lease takes items in <c>Id</c> order while their summed <c>TokenCount</c> stays within
/// <see cref="BatchTokenTarget"/> (and the count within <see cref="WorkLease.MaxItems"/>), and always at least one.
/// </summary>
public sealed record WorkBatchPolicy
{
    /// <exception cref="ArgumentOutOfRangeException">A value is negative.</exception>
    public WorkBatchPolicy(int eligibilityTokens, TimeSpan maxAge, int batchTokenTarget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(eligibilityTokens);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAge, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(batchTokenTarget);
        EligibilityTokens = eligibilityTokens;
        MaxAge = maxAge;
        BatchTokenTarget = batchTokenTarget;
    }

    /// <summary>Every claimable item is due at once, and each lease takes one item.</summary>
    public static WorkBatchPolicy Immediate { get; } = new(0, TimeSpan.Zero, 0);

    public int EligibilityTokens { get; }

    public TimeSpan MaxAge { get; }

    public int BatchTokenTarget { get; }
}
