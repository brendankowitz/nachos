using Shouldly;

namespace Nachos.Abstractions.Tests;

public sealed class PublicIdTests
{
    [Fact]
    public void PublicId_New_Is21UrlSafeChars()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => PublicId.New()).ToList();

        ids.ShouldAllBe(id => id.Length == 21 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-'));
        ids.Distinct().Count().ShouldBe(ids.Count);
    }
}