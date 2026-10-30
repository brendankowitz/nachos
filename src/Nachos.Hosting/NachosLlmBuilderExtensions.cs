using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nachos.Abstractions.Domain;
using Nachos.Core.Llm;
using Nachos.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

public static class NachosLlmBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="NachosChatClients"/>. <paramref name="createProviderClient"/> builds the raw provider client for a
    /// profile; Nachos wraps it with the profile options, fallback chain and telemetry. Profiles bind from <see cref="LlmOptions"/>;
    /// when no <see cref="LlmOptions.DefaultProfileName"/> profile is configured an empty one is added, so an agent with no
    /// <c>Llm:Agents</c> entry uses the provider's default chat model (for Azure OpenAI, <c>AzureOpenAI:ChatDeployment</c>).
    /// </summary>
    public static NachosBuilder UseChatClient(
        this NachosBuilder builder, Func<IServiceProvider, ModelProfile, IChatClient> createProviderClient)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(createProviderClient);
        builder.Services.AddOptions<LlmOptions>().BindConfiguration("Llm");
        builder.Services.PostConfigure<LlmOptions>(options =>
            options.Profiles.TryAdd(LlmOptions.DefaultProfileName, new ModelProfile()));
        builder.Services.Replace(ServiceDescriptor.Singleton(provider => new NachosChatClients(
            provider.GetRequiredService<IOptions<LlmOptions>>().Value,
            profile => createProviderClient(provider, profile),
            (provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger<NachosChatClients>())));
        return builder;
    }

    /// <summary>
    /// Registers the embedding generator, wrapped with OpenTelemetry (source <see cref="NachosChatClients.TelemetrySourceName"/>),
    /// and a startup check that it produces <see cref="VectorSpace.Dimensions"/> dimensions.
    /// <see cref="EmbeddingOptions.RequestDimensions"/> is applied to every request when set.
    /// </summary>
    public static NachosBuilder UseEmbeddingGenerator(
        this NachosBuilder builder, Func<IServiceProvider, IEmbeddingGenerator<string, Embedding<float>>> createGenerator)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(createGenerator);
        builder.Services.AddOptions<EmbeddingOptions>().BindConfiguration("Embeddings");
        builder.Services.Replace(ServiceDescriptor.Singleton(provider =>
        {
            var requestDimensions = provider.GetRequiredService<IOptions<EmbeddingOptions>>().Value.RequestDimensions;
            var pipeline = new EmbeddingGeneratorBuilder<string, Embedding<float>>(createGenerator(provider));
            if (requestDimensions is not null)
            {
                // Outside telemetry, so the recorded request carries the dimensions actually sent.
                pipeline.ConfigureOptions(options => options.Dimensions ??= requestDimensions);
            }

            return pipeline.UseOpenTelemetry(sourceName: NachosChatClients.TelemetrySourceName).Build(provider);
        }));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, EmbeddingDimensionCheck>());
        return builder;
    }
}
