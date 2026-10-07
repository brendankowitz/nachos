using Microsoft.Extensions.DependencyInjection;
using Nachos.Abstractions.Stores;

namespace Nachos.DataLayer.InMemory;

public static class InMemoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="InMemoryMemoryStore"/> as the singleton <see cref="IMemoryStore"/>, on the registered
    /// <see cref="TimeProvider"/> or <see cref="TimeProvider.System"/> when none is registered. The store is not
    /// durable: everything is lost when the process exits.
    /// </summary>
    public static IServiceCollection AddInMemoryMemoryStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSingleton<IMemoryStore>(
            provider => new InMemoryMemoryStore(provider.GetService<TimeProvider>() ?? TimeProvider.System));
    }
}
