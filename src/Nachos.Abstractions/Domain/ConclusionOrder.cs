namespace Nachos.Abstractions.Domain;

/// <summary>
/// The ascending sort key of a conclusion listing. Ties fall back to creation order (<c>CreatedAt</c>, then insertion),
/// and <see cref="PageRequest.Reverse"/> flips the whole order.
/// </summary>
public enum ConclusionOrder
{
    /// <summary>Oldest first.</summary>
    CreatedAt,

    /// <summary>Least corroborated first; reversed, the most frequently derived conclusions lead.</summary>
    TimesDerived,
}
