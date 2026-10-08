using Microsoft.Extensions.DependencyInjection.Extensions;
using Nachos.Abstractions;
using Nachos.Core;
using Nachos.Core.Configuration;
using Nachos.Core.Keys;
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
        services.TryAddScoped<NachosService>();
        services.TryAddScoped<INachosClient>(provider => provider.GetRequiredService<NachosService>());
        configure(new NachosBuilder(services));
        return services;
    }
}
