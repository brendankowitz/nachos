using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nachos.Abstractions;
using Nachos.Abstractions.Stores;
using Nachos.Core;
using Nachos.Core.Configuration;
using Nachos.Core.Keys;
using Nachos.Core.Memory;
using Nachos.Core.Tokens;
using Nachos.Core.Validation;
using Nachos.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

public static class NachosServiceCollectionExtensions
{
    /// <summary>
    /// Registers the complete scoped client and Core helpers without selecting a storage provider.
    /// </summary>
    public static IServiceCollection AddNachos(this IServiceCollection services, Action<NachosBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<NachosOptions>();
        services.AddOptions<SigningKeyOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IKeyIssuer, HmacKeyIssuer>();
        services.TryAddSingleton<ITokenCounter, TiktokenTokenCounter>();
        services.TryAddScoped<IConfigurationResolver, ConfigurationResolver>();
        services.TryAddScoped<RequestValidator>();
        // The embedding generator is optional (a host without AI still serves every other route), and a bare
        // container may have no logging.
        services.TryAddScoped(provider => new ConclusionService(
            provider.GetRequiredService<IMemoryStore>(),
            (provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger<ConclusionService>(),
            provider.GetService<IEmbeddingGenerator<string, Embedding<float>>>()));
        services.TryAddScoped(provider => new RepresentationBuilder(
            provider.GetRequiredService<IMemoryStore>(),
            provider.GetService<IEmbeddingGenerator<string, Embedding<float>>>()));
        services.TryAddScoped<PeerContextService>();
        services.TryAddScoped<NachosService>();
        services.TryAddScoped<INachosClient>(provider => provider.GetRequiredService<NachosService>());
        configure(new NachosBuilder(services));
        return services;
    }
}
