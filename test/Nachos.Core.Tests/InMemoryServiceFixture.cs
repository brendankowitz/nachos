using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions.Stores;
using Nachos.Core.Tokens;
using Nachos.DataLayer.InMemory;
using Shouldly;

namespace Nachos.Core.Tests;

internal sealed class InMemoryServiceFixture : IDisposable
{
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UnixEpoch);
    public ServiceProvider Provider { get; }
    public IServiceScope Scope { get; }
    public IMemoryStore Store { get; }
    public NachosService Service { get; }

    public InMemoryServiceFixture(ITokenCounter? counter = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<TimeProvider>(Clock);
        if (counter is not null) services.AddSingleton(counter);
        services.AddNachos(builder => builder.UseInMemory());
        Provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Scope = Provider.CreateScope();
        Store = Scope.ServiceProvider.GetRequiredService<IMemoryStore>();
        Store.ShouldBeOfType<InMemoryMemoryStore>();
        Service = Scope.ServiceProvider.GetRequiredService<NachosService>();
    }

    public async Task Seed()
    {
        await Service.GetOrCreateWorkspaceAsync("W");
        await Service.GetOrCreateSessionAsync("W", "S");
    }

    public async Task AssertNoMessagesOrPeers(string? key)
    {
        (await Service.ListMessagesAsync("W", "S", null, new())).Total.ShouldBe(0);
        (await Service.ListPeersAsync("W", null, null, new())).Total.ShouldBe(0);
        (await Service.ListSessionPeersAsync("W", "S", new())).Total.ShouldBe(0);
        if (key is not null) (await Store.Idempotency.TryGetAsync("W", key, default)).ShouldBeNull();
    }

    public static JsonElement Json(string text, int maxDepth = 64)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = maxDepth });
        return document.RootElement.Clone();
    }

    public void Dispose()
    {
        Scope.Dispose();
        Provider.Dispose();
    }
}

internal sealed class RecordingTokenCounter : ITokenCounter
{
    private readonly TiktokenTokenCounter _actual = new();
    public List<string> Inputs { get; } = [];
    public Exception? Failure { get; set; }

    public int Count(string text)
    {
        Inputs.Add(text);
        if (Failure is not null) throw Failure;
        return _actual.Count(text);
    }
}
