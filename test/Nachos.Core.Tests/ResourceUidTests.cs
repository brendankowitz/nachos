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
