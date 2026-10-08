using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.InMemory;
using Nachos.Hosting;

// Same namespace as AddNachos, so Program.cs needs no extra using to reach UseInMemory.
namespace Microsoft.Extensions.DependencyInjection;

public static class InMemoryBuilderExtensions
{
    /// <summary>
    /// Selects the non-durable in-memory store as the <see cref="IMemoryStore"/> provider.
    /// </summary>
    /// <remarks>
    /// The last provider selected wins, and calling this more than once is harmless. The store is a singleton built
    /// with the container's <see cref="TimeProvider"/>. Outside the Development environment a warning is logged at
    /// host start, because everything is lost on restart.
    /// </remarks>
    public static NachosBuilder UseInMemory(this NachosBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        services.RemoveAll<IMemoryStore>();
        services.AddSingleton<IMemoryStore>(sp => new InMemoryMemoryStore(sp.GetRequiredService<TimeProvider>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, InMemoryProviderNotice>());
        return builder;
    }
}
