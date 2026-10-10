using Shouldly;

namespace Nachos.Api.Tests;

public sealed class HttpDepthTests : ApiTest
{
    [Theory]
    [InlineData(999, 200)]
    [InlineData(1000, 200)]
    [InlineData(1001, 422)]
    public async Task UnknownWorkspaceField_CountsRootAsOne(int depth, int status)
    {
        var body = "{\"id\":\"w\",\"future\":" + new string('[', depth - 1) + "0" + new string(']', depth - 1) + "}";
        var result = await Post("/v3/workspaces", body, status);
        if (status == 422)
        {
            Location(result, "body");
            result.GetProperty("detail")[0].GetProperty("type").GetString().ShouldBe("json_invalid");
            (await Post("/v3/workspaces/list")).GetProperty("total").GetInt32().ShouldBe(0);
        }
        else result.GetProperty("id").GetString().ShouldBe("w");
    }
}
