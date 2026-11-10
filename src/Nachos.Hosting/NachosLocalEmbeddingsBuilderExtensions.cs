using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nachos.Core.Configuration;
using Nachos.Core.Llm;
using Nachos.Hosting;
using Nachos.Hosting.LocalEmbeddings;

namespace Microsoft.Extensions.DependencyInjection;

public static class NachosLocalEmbeddingsBuilderExtensions
{
    /// <summary>
    /// Embeds in-process on the CPU with a small sentence-embedding model (<see cref="LocalEmbeddingGenerator"/>), so no
    /// paid embedding service is needed. Settings bind from <c>Embeddings:Local</c> (<see cref="LocalEmbeddingOptions"/>),
    /// then <paramref name="configure"/> runs. The model's files must already be on disk (<c>nachos models fetch</c>);
    /// they are checked against their pinned SHA-256 when the generator is first resolved, at startup.
    /// </summary>
    /// <remarks>
    /// Message chunks use the model's own tokenizer and limit. When <c>Nachos:Deriver:DedupMaxDistance</c> is not
    /// configured, the model's calibrated <see cref="LocalEmbeddingModel.DedupMaxDistance"/> applies, because the default
    /// was chosen on another model's distance scale; the host binds <c>Nachos</c> after this, so an explicit setting wins.
    /// Chat still needs a chat client (or the fake AI). Call it after <c>UseAzureOpenAI</c> or <c>UseFakeAI</c> to replace
    /// their embedding generator.
    /// </remarks>
    public static NachosBuilder UseLocalEmbeddings(this NachosBuilder builder, Action<LocalEmbeddingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var known = string.Join(", ", LocalEmbeddingModel.All.Select(model => model.Id));
        builder.Services.AddOptions<LocalEmbeddingOptions>()
            .BindConfiguration("Embeddings:Local")
            .Configure(options => configure?.Invoke(options))
            .Validate(options => LocalEmbeddingModel.Find(options.Model) is not null, $"Embeddings:Local:Model must be one of: {known}.")
            .Validate(options => options.BatchSize >= 1, "Embeddings:Local:BatchSize must be at least 1.")
            .ValidateOnStart();
        builder.Services.AddOptions<NachosOptions>().Configure<IOptions<LocalEmbeddingOptions>>((nachos, local) =>
        {
            if (LocalEmbeddingModel.Find(local.Value.Model) is { } model)
            {
                nachos.Deriver.DedupMaxDistance = model.DedupMaxDistance;
            }
        });
        builder.Services.Replace(ServiceDescriptor.Singleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<LocalEmbeddingOptions>>().Value;
            var model = LocalEmbeddingModel.Find(options.Model)
                ?? throw new InvalidOperationException($"Embeddings:Local:Model must be one of: {known}.");
            return LocalEmbeddingGenerator.Create(
                model,
                options.ModelDirectory ?? LocalEmbeddingModel.DefaultDirectory,
                options.BatchSize,
                (provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger<LocalEmbeddingGenerator>());
        }));
        return builder.UseEmbeddingGenerator(
            provider => provider.GetRequiredService<LocalEmbeddingGenerator>(),
            provider => provider.GetRequiredService<LocalEmbeddingGenerator>().Chunking);
    }
}
