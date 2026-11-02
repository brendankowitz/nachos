using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nachos.Abstractions;
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
    /// Registers the complete scoped client and Core helpers without selecting a storage provider. The
    /// <see cref="NachosOptions"/> are validated at host start.
    /// </summary>
    public static IServiceCollection AddNachos(this IServiceCollection services, Action<NachosBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<NachosOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<NachosOptions>, NachosOptionsValidator>());
        services.AddOptions<SigningKeyOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IKeyIssuer, HmacKeyIssuer>();
        services.TryAddSingleton<ITokenCounter, TiktokenTokenCounter>();
        services.TryAddScoped<IConfigurationResolver, ConfigurationResolver>();
        services.TryAddScoped<RequestValidator>();
        // Their embedding generator and logger are optional constructor parameters: a host without AI still serves
        // every other route, and a bare container may have no logging.
        services.TryAddScoped<ConclusionService>();
        services.TryAddScoped<RepresentationBuilder>();
        services.TryAddScoped<PeerContextService>();
        services.TryAddScoped<NachosService>();
        services.TryAddScoped<INachosClient>(provider => provider.GetRequiredService<NachosService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, MissingEmbeddingGeneratorWarning>());
        configure(new NachosBuilder(services));
        return services;
    }

    private sealed class NachosOptionsValidator : IValidateOptions<NachosOptions>
    {
        public ValidateOptionsResult Validate(string? name, NachosOptions options) =>
            options.ValidationErrors() is { Count: > 0 } errors ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
