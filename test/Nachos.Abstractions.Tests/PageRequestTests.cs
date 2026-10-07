using Shouldly;

namespace Nachos.Abstractions.Tests;

public sealed class PageRequestTests
{
    [Fact]
    public void Defaults_AreFirstPageOfFifty()
    {
        new PageRequest().ShouldBe(new PageRequest(1, 50, false));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 100)]
    public void ValidValues_AreAccepted(int page, int size)
    {
        var request = new PageRequest(page, size, true);

        request.Page.ShouldBe(page);
        request.Size.ShouldBe(size);
        request.Reverse.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(-1, 50)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public void OutOfRangeValues_ThrowValidation(int page, int size)
    {
        Should.Throw<NachosValidationException>(() => new PageRequest(page, size));
    }
}