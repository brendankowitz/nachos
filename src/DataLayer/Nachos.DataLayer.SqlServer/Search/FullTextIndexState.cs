namespace Nachos.DataLayer.SqlServer.Search;

/// <summary>
/// Whether lexical message search can use the full-text index: one per process, written by
/// <see cref="FullTextIndexMaintenance"/>, read by every store. A new state is not ready, so a store without the
/// maintenance running always uses <c>LIKE</c>.
/// </summary>
public sealed class FullTextIndexState
{
    private volatile bool _isReady;

    /// <summary>True while the index exists, is enabled and has finished its first population, as of the last check.</summary>
    public bool IsReady
    {
        get => _isReady;
        internal set => _isReady = value;
    }
}
