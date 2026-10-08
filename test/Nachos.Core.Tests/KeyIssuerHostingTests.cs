using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using Nachos.Abstractions;
using Nachos.Core.Configuration;
using Nachos.Core.Keys;
using Nachos.Core.Validation;
using NSubstitute;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class KeyIssuerHostingTests
{
    [Fact]
    public void EmptyKeys_PermitTrustedLibraryDependenciesWithoutAPartialClient()
    {
        var services = new ServiceCollection();
        services.AddNachos(_ => { });
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        var issuer = scope.ServiceProvider.GetRequiredService<IKeyIssuer>();
        scope.ServiceProvider.GetRequiredService<IOptions<SigningKeyOptions>>().Value.Keys.ShouldBeEmpty();
        scope.ServiceProvider.GetRequiredService<RequestValidator>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IConfigurationResolver>().Resolve(null).ShouldNotBeNull();
        scope.ServiceProvider.GetService<INachosClient>().ShouldBeNull();
        Should.Throw<NachosValidationException>(() => issuer.Issue(new(true, null, null, null, null)));
    }

    [Fact]
    public void ConfiguredKeysAndClock_ArePreservedAndUsedByTheRegisteredIssuer()
    {
        var services = new ServiceCollection();
        services.Configure<SigningKeyOptions>(options => options.Keys =
            [new("configured", "synthetic-di-test-key-never-use-for-production-00000000000000000")]);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        services.AddNachos(_ => { });
        services.AddNachos(_ => { });
        using var provider = services.BuildServiceProvider();

        var issuer = provider.GetRequiredService<IKeyIssuer>();
        var claims = new NachosKeyClaims(true, null, null, null, null);
        var token = issuer.Issue(claims);
        using var header = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.Split('.')[0]));
        using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.Split('.')[1]));

        header.RootElement.GetProperty("kid").GetString().ShouldBe("configured");
        payload.RootElement.GetProperty("t").GetString().ShouldBe("1970-01-01T00:00:00.0000000Z");
        issuer.Validate(token).ShouldBe(claims);
        provider.GetServices<IKeyIssuer>().ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConsumerIssuerOverrides_ArePreserved(bool registerBefore)
    {
        var services = new ServiceCollection();
        var replacement = Substitute.For<IKeyIssuer>();
        if (registerBefore)
        {
            services.AddSingleton(replacement);
        }

        services.AddNachos(builder =>
        {
            if (!registerBefore)
            {
                builder.Services.AddSingleton(replacement);
            }
        });
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IKeyIssuer>().ShouldBeSameAs(replacement);
    }

    [Fact]
    public void InvalidSigningOptions_AreReportedAsConfigurationErrorsOnResolution()
    {
        var services = new ServiceCollection();
        services.AddNachos(builder => builder.Services.Configure<SigningKeyOptions>(
            options => options.Keys = [new("short", "synthetic-short-key")]));
        using var provider = services.BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IKeyIssuer>());
    }
}
