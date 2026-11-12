using Shouldly;

namespace Nachos.Core.Tests;

public sealed class ResourceUidTests
{
    private static readonly DateTimeOffset Created = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void SameInputs_SameUid_IndependentOfOffset()
    {
        ResourceUid.ForPeer("W", "P", Created).ShouldBe(ResourceUid.ForPeer("W", "P", Created));
        ResourceUid.ForPeer("W", "P", Created).ShouldBe(ResourceUid.ForPeer("W", "P", Created.ToOffset(TimeSpan.FromHours(5))));
        ResourceUid.ForSession("W", "S", Created).ShouldMatch("^[0-9a-f]{32}$");
    }

    // Computed independently: hex(SHA-256(kind NUL workspace NUL name NUL UtcTicks))[..32]. A change here changes every
    // uid already handed to clients.
    [Fact]
    public void GoldenVectors()
    {
        Created.UtcTicks.ShouldBe(639029198450000000);
        ResourceUid.ForWorkspace("W", Created).ShouldBe("d08f9e67efa921037204044f455cc9a1");
        ResourceUid.ForPeer("W", "P", Created).ShouldBe("a6917558b03d32f665957ba5d8a93321");
        ResourceUid.ForSession("W", "S", Created).ShouldBe("d0dcdfd663216dfe6e007349dbd27853");
    }

    [Fact]
    public void EachInput_ChangesTheUid()
    {
        string[] uids =
        [
            ResourceUid.ForPeer("W", "P", Created),
            ResourceUid.ForSession("W", "P", Created),
            ResourceUid.ForPeer("V", "P", Created),
            ResourceUid.ForPeer("W", "Q", Created),
            ResourceUid.ForPeer("W", "P", Created.AddTicks(1)),
            ResourceUid.ForWorkspace("W", Created),
        ];

        uids.Distinct().Count().ShouldBe(uids.Length);
    }
}
