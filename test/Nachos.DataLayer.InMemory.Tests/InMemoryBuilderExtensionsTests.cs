using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions.Stores;
using Nachos.Hosting;
using Shouldly;

namespace Nachos.DataLayer.InMemory.Tests;

public sealed class InMemoryBuilderExtensionsTests
{
    private const string NoticeText = "in-memory provider is not durable";

    private static CancellationToken Ct => CancellationToken.None;

    private static ServiceCollection NewServices(string? environment, LogSink sink, TimeProvider? clock = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sink);
        services.AddSingleton(typeof(ILogger<>), typeof(CapturingLogger<>));
        if (environment is not null)
            services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment(environment));
        if (clock is not null)
            services.AddSingleton(clock);
        return services;
    }

    private static async Task StartHostedServicesAsync(IServiceProvider provider)
    {
        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(Ct);
    }

    [Fact]
    public async Task UseInMemory_OutsideDevelopment_LogsWarning()
    {
        var sink = new LogSink();
        var services = NewServices(Environments.Production, sink);
        services.AddNachos(b => b.UseInMemory());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        await StartHostedServicesAsync(provider);

        var entry = sink.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain(NoticeText);
        entry.Message.ShouldContain("SQL Server");
    }

    [Fact]
    public async Task UseInMemory_InDevelopment_LogsNothing()
    {
        var sink = new LogSink();
        var services = NewServices(Environments.Development, sink);
        services.AddNachos(b => b.UseInMemory());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        await StartHostedServicesAsync(provider);

        sink.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task UseInMemory_WithoutHostEnvironment_Warns()
    {
        var sink = new LogSink();
        var services = NewServices(environment: null, sink);
        services.AddNachos(b => b.UseInMemory());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        await StartHostedServicesAsync(provider);

        var entry = sink.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain(NoticeText);
    }

    [Fact]
    public async Task UseInMemory_ResolvesAWorkingSingletonStore()
    {
        var services = NewServices(Environments.Development, new LogSink());
        services.AddNachos(b => b.UseInMemory());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        IMemoryStore first, second;
        using (var scope = provider.CreateScope())
            first = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
        using (var scope = provider.CreateScope())
            second = scope.ServiceProvider.GetRequiredService<IMemoryStore>();

        second.ShouldBeSameAs(first);
        first.ShouldBeOfType<InMemoryMemoryStore>();
        var created = await first.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
        var fetched = await second.Workspaces.GetAsync("ws", Ct);
        fetched.ShouldNotBeNull();
        fetched.Name.ShouldBe(created.Name);
        fetched.CreatedAt.ShouldBe(created.CreatedAt);
    }

    [Fact]
    public async Task UseInMemory_UsesTheContainersTimeProvider()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero));
        var services = NewServices(Environments.Development, new LogSink(), clock);
        services.AddNachos(b => b.UseInMemory());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var store = provider.GetRequiredService<IMemoryStore>();
        var workspace = await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);

        workspace.CreatedAt.ShouldBe(clock.GetUtcNow());
    }

    [Fact]
    public void UseInMemory_CalledTwice_RegistersOneStoreAndOneNotice()
    {
        var sink = new LogSink();
        var services = NewServices(Environments.Production, sink);
        services.AddNachos(b => b.UseInMemory().UseInMemory());

        services.Count(d => d.ServiceType == typeof(IMemoryStore)).ShouldBe(1);
        services.Count(d => d.ServiceType == typeof(IHostedService)).ShouldBe(1);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        provider.GetServices<IHostedService>().Count().ShouldBe(1);
        provider.GetServices<IMemoryStore>().Count().ShouldBe(1);
    }

    [Fact]
    public void UseInMemory_ExposesOnlyTheMemoryStoreContract()
    {
        var services = NewServices(Environments.Development, new LogSink());
        services.AddNachos(b => b.UseInMemory());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        provider.GetService<InMemoryMemoryStore>().ShouldBeNull();
    }

    [Fact]
    public void UseInMemory_ReplacesAnEarlierMemoryStore()
    {
        var services = NewServices(Environments.Development, new LogSink());
        var earlier = new InMemoryMemoryStore(TimeProvider.System);
        services.AddSingleton<IMemoryStore>(earlier);
        services.AddSingleton<IMemoryStore>(new InMemoryMemoryStore(TimeProvider.System));
        services.AddNachos(b => b.UseInMemory());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        services.Count(d => d.ServiceType == typeof(IMemoryStore)).ShouldBe(1);
        provider.GetRequiredService<IMemoryStore>().ShouldNotBeSameAs(earlier);
    }

    [Fact]
    public void UseInMemory_NullBuilder_Throws()
    {
        NachosBuilder builder = null!;

        Should.Throw<ArgumentNullException>(() => builder.UseInMemory()).ParamName.ShouldBe("builder");
    }

    [Fact]
    public void UseInMemory_ReturnsTheSameBuilder()
    {
        var builder = new NachosBuilder(new ServiceCollection());

        builder.UseInMemory().ShouldBeSameAs(builder);
    }

    private sealed class LogSink
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries) return [.. _entries];
            }
        }

        public void Add(LogEntry entry)
        {
            lock (_entries) _entries.Add(entry);
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger<T>(LogSink sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            sink.Add(new LogEntry(logLevel, formatter(state, exception)));
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Nachos.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
