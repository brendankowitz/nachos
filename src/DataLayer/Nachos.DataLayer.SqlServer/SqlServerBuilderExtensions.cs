using Microsoft.Extensions.DependencyInjection.Extensions;
using Nachos.Abstractions.Schema;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.SqlServer;
using Nachos.DataLayer.SqlServer.Schema;
using Nachos.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

public static class SqlServerBuilderExtensions
{
    /// <summary>
    /// Selects the SQL Server provider: <see cref="IMemoryStore"/> is a scoped <see cref="SqlMemoryStore"/>,
    /// <see cref="ISchemaManager"/> the dacpac <see cref="SchemaDeployer"/>, and one <see cref="SchemaGate"/> per
    /// container verifies (and, when <see cref="SqlServerOptions.AutomaticSchemaDeploymentEnabled"/>, deploys) the schema
    /// before the first store operation.
    /// </summary>
    /// <remarks>
    /// The last provider selected wins: any earlier <see cref="IMemoryStore"/> registration is removed. Calling this again
    /// registers nothing twice; its <paramref name="configure"/> is applied to the same <see cref="SqlServerOptions"/>
    /// instance, after the earlier calls'. The options are read when the container is built, so configure them during
    /// registration only.
    /// </remarks>
    public static NachosBuilder UseSqlServer(this NachosBuilder builder, Action<SqlServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var services = builder.Services;
        var options = services
            .Where(descriptor => descriptor.ServiceType == typeof(SqlServerOptions))
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<SqlServerOptions>()
            .FirstOrDefault();
        if (options is null)
        {
            options = new SqlServerOptions();
            services.AddSingleton(options);
        }

        configure(options);

        services.TryAddSingleton(TimeProvider.System);
        services.RemoveAll<ISchemaManager>();
        services.AddSingleton<ISchemaManager, SchemaDeployer>();
        services.RemoveAll<SchemaGate>();
        services.AddSingleton(provider => new SchemaGate(
            provider.GetRequiredService<ISchemaManager>(), provider.GetRequiredService<SqlServerOptions>()));
        services.RemoveAll<IMemoryStore>();
        services.AddScoped<IMemoryStore>(provider => new SqlMemoryStore(
            provider.GetRequiredService<SqlServerOptions>(),
            provider.GetRequiredService<SchemaGate>(),
            provider.GetRequiredService<TimeProvider>()));
        return builder;
    }
}
