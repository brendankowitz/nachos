using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Abstractions.Stores;
using Nachos.Core.Configuration;
using Nachos.Core.Tokens;
using Nachos.Core.Validation;
using NSubstitute;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class HostingTests
{
    [Fact]
    public void AddNachos_RegistersCompleteScopedClientAndExposesProviderServices()
    {
        var services = new ServiceCollection();
        var store = Substitute.For<IMemoryStore>();
        var result = services.AddNachos(builder =>
        {
            builder.Services.ShouldBeSameAs(services);
            builder.Services.AddSingleton(store);
            builder.Services.Configure<NachosOptions>(options => options.Summary.MessagesPerShort = 37);
        });
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        result.ShouldBeSameAs(services);
        scope.ServiceProvider.GetRequiredService<IMemoryStore>().ShouldBeSameAs(store);
        scope.ServiceProvider.GetRequiredService<ITokenCounter>().Count("hello world").ShouldBe(2);
        scope.ServiceProvider.GetRequiredService<IConfigurationResolver>()
            .Resolve(null).Summary.MessagesPerShortSummary.Value.ShouldBe(37);
        scope.ServiceProvider.GetRequiredService<RequestValidator>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IOptions<NachosOptions>>().Value.Summary.MessagesPerShort.ShouldBe(37);
        scope.ServiceProvider.GetRequiredService<TimeProvider>().ShouldBeSameAs(TimeProvider.System);
        scope.ServiceProvider.GetRequiredService<INachosClient>()
            .ShouldBeSameAs(scope.ServiceProvider.GetRequiredService<NachosService>());
        using var otherScope = provider.CreateScope();
        otherScope.ServiceProvider.GetRequiredService<INachosClient>()
            .ShouldNotBeSameAs(scope.ServiceProvider.GetRequiredService<INachosClient>());
    }

    [Fact]
    public void PreconfiguredOverridesAndOptionsArePreserved()
    {
        var counter = Substitute.For<ITokenCounter>();
        var resolver = Substitute.For<IConfigurationResolver>();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var services = new ServiceCollection();
        services.AddSingleton(counter);
        services.AddSingleton(resolver);
        services.AddSingleton<TimeProvider>(clock);
        services.Configure<NachosOptions>(options => options.Summary.MessagesPerShort = 45);

        services.AddNachos(_ => { });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITokenCounter>().ShouldBeSameAs(counter);
        scope.ServiceProvider.GetRequiredService<IConfigurationResolver>().ShouldBeSameAs(resolver);
        scope.ServiceProvider.GetRequiredService<TimeProvider>().ShouldBeSameAs(clock);
        scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().ShouldBe(DateTimeOffset.UnixEpoch);
        scope.ServiceProvider.GetRequiredService<IOptions<NachosOptions>>().Value.Summary.MessagesPerShort.ShouldBe(45);
        scope.ServiceProvider.GetService<IMemoryStore>().ShouldBeNull();
    }

    [Fact]
    public void BuilderCanOverrideDefaultsAfterRegistration()
    {
        var counter = Substitute.For<ITokenCounter>();
        var services = new ServiceCollection();
        services.AddNachos(builder => builder.Services.AddSingleton(counter));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ITokenCounter>().ShouldBeSameAs(counter);
    }

    [Fact]
    public void RepeatedRegistrationIsIdempotentAndResolverIsScoped()
    {
        var services = new ServiceCollection();
        services.AddNachos(_ => { });
        services.AddNachos(_ => { });
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        first.ServiceProvider.GetServices<ITokenCounter>().ShouldHaveSingleItem();
        first.ServiceProvider.GetServices<IConfigurationResolver>().ShouldHaveSingleItem();
        first.ServiceProvider.GetRequiredService<ITokenCounter>()
            .ShouldBeSameAs(second.ServiceProvider.GetRequiredService<ITokenCounter>());
        first.ServiceProvider.GetRequiredService<IConfigurationResolver>()
            .ShouldBeSameAs(first.ServiceProvider.GetRequiredService<IConfigurationResolver>());
        first.ServiceProvider.GetRequiredService<IConfigurationResolver>()
            .ShouldNotBeSameAs(second.ServiceProvider.GetRequiredService<IConfigurationResolver>());
    }

    [Fact]
    public void InvalidDeploymentOptionsAreRejectedWhenHelpersResolve()
    {
        var services = new ServiceCollection();
        services.AddNachos(builder => builder.Services.Configure<NachosOptions>(
            options => options.Summary.MessagesPerShort = 9));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Should.Throw<OptionsValidationException>(() =>
            scope.ServiceProvider.GetRequiredService<IConfigurationResolver>());
    }

    [Fact]
    public void NullConfigureIsRejectedBeforeAddingServices()
    {
        var services = new ServiceCollection();

        Should.Throw<ArgumentNullException>(() => services.AddNachos(null!));
        services.ShouldBeEmpty();
    }
}
