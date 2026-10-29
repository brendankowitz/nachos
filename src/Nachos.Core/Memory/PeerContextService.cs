using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;

namespace Nachos.Core.Memory;

/// <summary>
/// What one peer (the observer) knows about another (the observed): its peer card and its working representation.
/// Every read takes the request's <see cref="VisibilityPolicy"/>.
/// </summary>
public sealed class PeerContextService(IMemoryStore store, RepresentationBuilder representations)
{
    /// <summary>The card's entries, or null when there is none or the policy hides cards.</summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    public async Task<IReadOnlyList<string>?> GetCardAsync(
        string workspaceName, string observerName, string observedName, VisibilityPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.IncludesPeerCard)
        {
            return null;
        }

        var card = await store.PeerCards.GetAsync(workspaceName, observerName, observedName, ct).ConfigureAwait(false);
        return card?.Entries;
    }

    /// <summary>Replaces the card's entries with <paramref name="entries"/>, as sent: no cap or prefix rule (owner decision Q4).</summary>
    /// <exception cref="NotFoundException">The workspace or either peer does not exist.</exception>
    public Task<PeerCardRecord> SetCardAsync(
        string workspaceName, string observerName, string observedName, IReadOnlyList<string> entries, CancellationToken ct) =>
        store.PeerCards.SetAsync(workspaceName, observerName, observedName, entries, PeerCardAuthor.Api, ct);

    /// <summary>The working representation under <paramref name="policy"/> (see <see cref="RepresentationBuilder"/>).</summary>
    public Task<string> GetRepresentationAsync(
        string workspaceName,
        string observerName,
        string observedName,
        VisibilityPolicy policy,
        RepresentationQuery query,
        CancellationToken ct) =>
        representations.BuildAsync(workspaceName, observerName, observedName, policy, query, ct);

    /// <summary>The representation and the card, both under <paramref name="policy"/>.</summary>
    public async Task<PeerContext> GetContextAsync(
        string workspaceName,
        string observerName,
        string observedName,
        VisibilityPolicy policy,
        RepresentationQuery query,
        CancellationToken ct)
    {
        var card = await GetCardAsync(workspaceName, observerName, observedName, policy, ct).ConfigureAwait(false);
        var representation = await GetRepresentationAsync(workspaceName, observerName, observedName, policy, query, ct)
            .ConfigureAwait(false);
        return new(observerName, observedName, representation, card);
    }
}
