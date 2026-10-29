using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nachos.Core.Llm;
using Nachos.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

public static class NachosLlmBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="NachosChatClients"/>. <paramref name="createProviderClient"/> builds the raw provider client for a
    /// profile; Nachos wraps it with the profile options, fallback chain and telemetry. Profiles bind from <see cref="LlmOptions"/>.
    /// </summary>
    public static NachosBuilder UseChatClient(
        this NachosBuilder builder, Func<IServiceProvider, ModelProfile, IChatClient> createProviderClient)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(createProviderClient);
        builder.Services.AddOptions<LlmOptions>();
        builder.Services.Replace(ServiceDescriptor.Singleton(provider => new NachosChatClients(
            provider.GetRequiredService<IOptions<LlmOptions>>().Value,
            profile => createProviderClient(provider, profile))));
        return builder;
    }

    /// <summary>
    /// Registers the embedding generator and a startup check that it produces <see cref="EmbeddingOptions.RequiredDimensions"/>
    /// dimensions. <see cref="EmbeddingOptions.RequestDimensions"/> is applied to every request when set.
    /// </summary>
    public static NachosBuilder UseEmbeddingGenerator(
        this NachosBuilder builder, Func<IServiceProvider, IEmbeddingGenerator<string, Embedding<float>>> createGenerator)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(createGenerator);
        builder.Services.AddOptions<EmbeddingOptions>();
        builder.Services.Replace(ServiceDescriptor.Singleton(provider =>
        {
            var requestDimensions = provider.GetRequiredService<IOptions<EmbeddingOptions>>().Value.RequestDimensions;
            var generator = createGenerator(provider);
            return requestDimensions is null
                ? generator
                : new EmbeddingGeneratorBuilder<string, Embedding<float>>(generator)
                    .ConfigureOptions(options => options.Dimensions ??= requestDimensions)
                    .Build();
        }));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, EmbeddingDimensionCheck>());
        return builder;
    }
}
