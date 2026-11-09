namespace Nachos.Core.Context;

/// <summary>
/// The token budget of one session context (spec §11.6), in content tokens (M3 D5). Every part of the context is
/// reserved here before it is added, and a reservation that does not fit is refused, so what is reserved never exceeds
/// <see cref="Tokens"/>.
/// </summary>
public sealed class ContextBudget
{
    /// <summary>The largest budget a request may ask for, and the budget when it names none.</summary>
    public const int MaxTokens = 100_000;

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tokens"/> is not 1 to <see cref="MaxTokens"/>.</exception>
    public ContextBudget(int tokens)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tokens, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tokens, MaxTokens);
        Tokens = tokens;
        Remaining = tokens;
    }

    public int Tokens { get; }

    public int Remaining { get; private set; }

    /// <summary>What the representation and card may take together: half of the whole budget, rounded down.</summary>
    public int PeerShare => Tokens / 2;

    /// <summary>What the summary may take: 40% of what is left when it is chosen, rounded down.</summary>
    public int SummaryShare => (int)(Remaining * 2L / 5);

    /// <summary>Reserves <paramref name="tokens"/> when they fit in <see cref="Remaining"/>; otherwise reserves nothing.</summary>
    public bool TryReserve(int tokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tokens);
        if (tokens > Remaining)
        {
            return false;
        }

        Remaining -= tokens;
        return true;
    }
}
