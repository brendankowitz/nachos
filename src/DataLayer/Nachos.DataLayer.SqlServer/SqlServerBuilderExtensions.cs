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
    /// before the first store operation. The options are read once, when this method runs.
    /// </summary>
    public static NachosBuilder UseSqlServer(this NachosBuilder builder, Action<SqlServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SqlServerOptions();
        configure(options);

        var services = builder.Services;
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton<ISchemaManager, SchemaDeployer>();
        services.AddSingleton(provider => new SchemaGate(provider.GetRequiredService<ISchemaManager>(), options));
        services.AddScoped<IMemoryStore>(provider => new SqlMemoryStore(
            options, provider.GetRequiredService<SchemaGate>(), provider.GetRequiredService<TimeProvider>()));
        return builder;
    }
}
