using Nachos.Abstractions;

namespace Nachos.Client.Tests;

/// <summary>
/// The <see cref="INachosClient"/> operations whose HTTP implementation is still a <see cref="NotSupportedException"/>
/// shim. The coverage gates skip exactly these; M2 Task 8 implements them, adds their shape and round-trip tests, and
/// deletes this list.
/// </summary>
internal static class PendingClientOperations
{
    public static readonly IReadOnlySet<string> Task8 = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(INachosClient.CreateConclusionsAsync),
        nameof(INachosClient.ListConclusionsAsync),
        nameof(INachosClient.QueryConclusionsAsync),
        nameof(INachosClient.GetConclusionAsync),
        nameof(INachosClient.DeleteConclusionAsync),
        nameof(INachosClient.GetPeerCardAsync),
        nameof(INachosClient.SetPeerCardAsync),
        nameof(INachosClient.GetRepresentationAsync),
        nameof(INachosClient.GetPeerContextAsync),
        nameof(INachosClient.GetQueueStatusAsync),
    };
}
