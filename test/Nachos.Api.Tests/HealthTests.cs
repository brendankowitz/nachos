using Shouldly;

namespace Nachos.Api.Tests;

public sealed class HealthTests : ApiTest
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/health")]
    public async Task Live_Ready_Alias_Return200(string path)
    {
        using var response = await Client.GetAsync(path);
        ((int)response.StatusCode).ShouldBe(200);
        (await response.Content.ReadAsStringAsync()).ShouldBe("Healthy");
    }
}
