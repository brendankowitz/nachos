using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.InMemory;
using Nachos.DataLayer.SqlServer;
using Nachos.DataLayer.SqlServer.Schema;
using Shouldly;

namespace Nachos.Api.Tests;

/// <summary>
/// Which provider the API registers for a given configuration. Only the registration is inspected (the SQL store opens no
/// connection until it is used), so no database is needed.
/// </summary>
public sealed class ProviderSelectionTests
{
    private const string Canonical = "Nachos:SqlServer:ConnectionString";
    private const string Aspire = "ConnectionStrings:nachos";
    private const string CanonicalConnection = "Server=canonical.example;Database=canon;User Id=sa;Password=Canon-Secret-1;";
    private const string AspireConnection = "Server=aspire.example;Database=asp;User Id=sa;Password=Aspire-Secret-1;";

    [Fact]
    public void NoConnection_UsesInMemory()
    {
        using var host = new ConfiguredFactory();

        host.Store().ShouldBeOfType<InMemoryMemoryStore>();
        host.Services.GetService<SqlServerOptions>().ShouldBeNull();
    }

    [Fact]
    public void CanonicalConnection_UsesSqlServer()
    {
        using var host = new ConfiguredFactory((Canonical, CanonicalConnection));

        host.Store().ShouldBeOfType<SqlMemoryStore>();
        host.Services.GetRequiredService<SqlServerOptions>().ConnectionString.ShouldBe(CanonicalConnection);
    }

    [Fact]
    public void AspireConnection_UsesSqlServer()
    {
        using var host = new ConfiguredFactory((Aspire, AspireConnection));

        host.Store().ShouldBeOfType<SqlMemoryStore>();
        host.Services.GetRequiredService<SqlServerOptions>().ConnectionString.ShouldBe(AspireConnection);
    }

    [Fact]
    public void Both_CanonicalWins()
    {
        using var host = new ConfiguredFactory((Aspire, AspireConnection), (Canonical, CanonicalConnection));

        host.Store().ShouldBeOfType<SqlMemoryStore>();
        host.Services.GetRequiredService<SqlServerOptions>().ConnectionString.ShouldBe(CanonicalConnection);
    }

    [Fact]
    public void SectionOptions_BindAndDefaultToNoAutomaticDeployment()
    {
        using var defaults = new ConfiguredFactory((Aspire, AspireConnection));
        using var enabled = new ConfiguredFactory(
            (Aspire, AspireConnection), ("Nachos:SqlServer:AutomaticSchemaDeploymentEnabled", "true"));

        defaults.Services.GetRequiredService<SqlServerOptions>().AutomaticSchemaDeploymentEnabled.ShouldBeFalse();
        enabled.Services.GetRequiredService<SqlServerOptions>().AutomaticSchemaDeploymentEnabled.ShouldBeTrue();
    }

    [Theory]
    [InlineData(Canonical, "")]
    [InlineData(Canonical, "   ")]
    [InlineData(Canonical, "this is not a connection string")]
    [InlineData(Canonical, "Server=db.example;User Id=sa;Password=No-Database-Secret-1;")]
    [InlineData(Aspire, "")]
    [InlineData(Aspire, "   ")]
    [InlineData(Aspire, "Server=db.example;Password=Garbage-Secret-1;Bogus Keyword=1;Database=x")]
    [InlineData(Aspire, "Server=db.example;User Id=sa;Password=No-Database-Secret-2;")]
    public void UnusableConnection_FailsStartupWithoutEchoingTheValue(string key, string value)
    {
        using var host = new ConfiguredFactory((key, value));

        var message = Should.Throw<Exception>(() => host.Services).ChainMessages();

        message.ShouldContain($"'{key}'");
        message.ShouldContain("names a database");
        if (value.Trim().Length > 0)
        {
            message.ShouldNotContain(value);
            message.ShouldNotContain("Secret");
        }
    }

    [Fact]
    public void UnusableCanonical_DoesNotFallBackToAValidAspireConnection()
    {
        using var host = new ConfiguredFactory((Canonical, ""), (Aspire, AspireConnection));

        Should.Throw<Exception>(() => host.Services).ChainMessages().ShouldContain($"'{Canonical}'");
    }

    [Fact]
    public void InvalidSectionOption_FailsStartup()
    {
        using var host = new ConfiguredFactory(
            (Aspire, AspireConnection), ("Nachos:SqlServer:AutomaticSchemaDeploymentEnabled", "maybe"));

        Should.Throw<Exception>(() => host.Services);
    }

    /// <summary>The API in Development with auth off and exactly the given settings, and no provider override.</summary>
    private sealed class ConfiguredFactory(params (string Key, string Value)[] settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Nachos:Auth:Enabled", "false");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        }

        public IMemoryStore Store()
        {
            using var scope = Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<IMemoryStore>();
        }
    }
}

internal static class ExceptionMessages
{
    /// <summary>Every message in the exception chain, joined; host start-up wraps the original failure.</summary>
    internal static string ChainMessages(this Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }
        return string.Join(" | ", messages);
    }
}
