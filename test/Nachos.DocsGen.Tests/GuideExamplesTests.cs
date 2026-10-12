using Microsoft.Extensions.DependencyInjection;
using Nachos.Abstractions;
using Shouldly;

namespace Nachos.DocsGen.Tests;

public sealed class GuideExamplesTests
{
    [Fact]
    public async Task LibraryGuide_CreatesWorkspaceThroughTheRealInProcessClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNachos(nachos => nachos.UseInMemory());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<INachosClient>();
        var workspace = await client.GetOrCreateWorkspaceAsync("demo");
        workspace.Id.ShouldBe("demo");
    }
}
