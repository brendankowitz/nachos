using Nachos.Abstractions.Contracts;
using Shouldly;

namespace Nachos.Abstractions.Tests;

public sealed class PageRequestTests
{
    [Fact]
    public void Defaults_AreFirstPageOfFifty()
    {
        new PageRequest().ShouldBe(new PageRequest(1, 50, false));
        PageRequest.DefaultSize.ShouldBe(50);
        PageRequest.MaxSize.ShouldBe(100);
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
    [InlineData(0, 50, "page", "greater_than_equal")]
    [InlineData(-1, 50, "page", "greater_than_equal")]
    [InlineData(1, 0, "size", "greater_than_equal")]
    [InlineData(1, 101, "size", "less_than_equal")]
    public void OutOfRangeValues_ThrowRequestValidation(int page, int size, string field, string type)
    {
        var ex = Should.Throw<RequestValidationException>(() => new PageRequest(page, size));

        var error = ex.Errors.ShouldHaveSingleItem();
        error.Loc.Select(x => x.ToString()).ShouldBe(["query", field]);
        error.Type.ShouldBe(type);
        error.Msg.ShouldStartWith("Input should be ");
    }

    [Fact]
    public void PageRequest_With_InvalidSize_Throws()
    {
        var valid = new PageRequest();

        var ex = Should.Throw<RequestValidationException>(() => valid with { Size = 101 });

        ex.Errors.Single().Loc.Select(x => x.ToString()).ShouldBe(["query", "size"]);
    }

    [Fact]
    public void PageRequest_ObjectInitializer_InvalidPage_Throws()
    {
        var ex = Should.Throw<RequestValidationException>(() => new PageRequest { Page = 0 });

        ex.Errors.Single().Loc.Select(x => x.ToString()).ShouldBe(["query", "page"]);
    }

    [Fact]
    public void PageRequest_With_ValidValues_Works()
    {
        (new PageRequest() with { Page = 3, Size = 10, Reverse = true }).ShouldBe(new PageRequest(3, 10, true));
    }
}