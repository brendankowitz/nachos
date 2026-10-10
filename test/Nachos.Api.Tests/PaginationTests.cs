using Shouldly;

namespace Nachos.Api.Tests;

public sealed class PaginationTests : ApiTest
{
    [Fact]
    public async Task PageBeyondEnd_ReturnsEmptyWithTotals()
    {
        await Post("/v3/workspaces", """{"id":"w"}""");
        var page = await Post("/v3/workspaces/list?page=3&size=1");
        Ids(page).ShouldBeEmpty();
        page.GetProperty("total").GetInt64().ShouldBe(1);
        page.GetProperty("pages").GetInt32().ShouldBe(1);
        page.GetProperty("page").GetInt32().ShouldBe(3);
        page.GetProperty("size").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task SizeAbove100_Returns422()
    {
        Location(await Post("/v3/workspaces/list?size=101", status: 422), "query", "size");
    }

    [Fact]
    public async Task EmptyCollection_PagesZero()
    {
        var page = await Post("/v3/workspaces/list", """{"page":4,"size":1,"reverse":true}""");
        Ids(page).ShouldBeEmpty();
        page.GetProperty("pages").GetInt32().ShouldBe(0);
        page.GetProperty("total").GetInt64().ShouldBe(0);
        page.GetProperty("page").GetInt32().ShouldBe(1);
        page.GetProperty("size").GetInt32().ShouldBe(50);
    }

    [Fact]
    public async Task Reverse_FlipsOrder()
    {
        await Post("/v3/workspaces", """{"id":"first"}""");
        await Post("/v3/workspaces", """{"id":"second"}""");
        Ids(await Post("/v3/workspaces/list?reverse=true")).ShouldBe(["second", "first"]);
    }

    [Theory]
    [InlineData("page=wat", "page")]
    [InlineData("page=0", "page")]
    [InlineData("size=", "size")]
    [InlineData("size=0", "size")]
    [InlineData("page=99999999999999999", "page")]
    [InlineData("reverse=wat", "reverse")]
    public async Task MalformedQuery_IsLocated422NotEmpty400(string query, string field)
    {
        Location(await Post("/v3/workspaces/list?" + query, status: 422), "query", field);
    }
}
