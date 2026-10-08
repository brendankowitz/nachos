using Microsoft.Extensions.DependencyInjection;
using Nachos.Abstractions.Schema;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.SqlServer.Schema;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary><c>UseSqlServer</c> registrations, and the schema gate running before the first store operation.</summary>
[Collection(SqlServerDockerGroup.Name)]
public sealed class SqlServerBuilderExtensionsTests(SqlServerFixture fixture)
{
    [Fact]
    public void UseSqlServer_RegistersScopedStoreSchemaManagerAndGate()
    {
        var services = new ServiceCollection();
        services.AddNachos(nachos => nachos.UseSqlServer(options =>
        {
            options.ConnectionString = "Server=unused;Database=unused";
            options.AutomaticSchemaDeploymentEnabled = true;
        }));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var store = first.ServiceProvider.GetRequiredService<IMemoryStore>();
        store.ShouldBeOfType<SqlMemoryStore>();
        first.ServiceProvider.GetRequiredService<IMemoryStore>().ShouldBeSameAs(store);
        second.ServiceProvider.GetRequiredService<IMemoryStore>().ShouldNotBeSameAs(store);

        provider.GetRequiredService<ISchemaManager>().ShouldBeOfType<SchemaDeployer>();
        provider.GetRequiredService<SchemaGate>().ShouldBeSameAs(provider.GetRequiredService<SchemaGate>());
        var options = provider.GetRequiredService<SqlServerOptions>();
        options.ConnectionString.ShouldBe("Server=unused;Database=unused");
        options.AutomaticSchemaDeploymentEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task FirstStoreOperation_RunsTheSchemaGate()
    {
        // An empty database with automatic deployment off: the gate refuses before the store touches any table.
        var services = new ServiceCollection();
        var connectionString = await fixture.CreateDatabaseAsync();
        services.AddNachos(nachos => nachos.UseSqlServer(options => options.ConnectionString = connectionString));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMemoryStore>();

        var refused = await Should.ThrowAsync<InvalidOperationException>(() => store.Workspaces.GetAsync("any", CancellationToken.None));

        refused.Message.ShouldContain("nachos schema upgrade");
    }
}
