using Microsoft.Extensions.DependencyInjection.Extensions;
using Nachos.Core.Deriver;
using Nachos.Core.Llm;
using Nachos.Core.Worker;
using Nachos.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

public static class NachosDeriverBuilderExtensions
{
    /// <summary>
    /// Registers the Deriver as the scoped <see cref="IWorkHandler"/> for representation work. It needs a chat client
    /// (<c>UseChatClient</c>, <c>UseAzureOpenAI</c> or <see cref="UseFakeAI"/>) and an embedding generator.
    /// </summary>
    public static NachosBuilder AddDeriver(this NachosBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<WorkerMetrics>();
        builder.Services.TryAddScoped<ConclusionDeduplicator>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkHandler, DeriverService>());
        return builder;
    }

    /// <summary>
    /// Offline AI for local runs and demos without a model: the <see cref="DeterministicEmbeddingGenerator"/>, and a chat
    /// client that answers each Deriver request by returning every target-peer message, verbatim, as one fact sourced
    /// from that message. Other agents' requests throw <see cref="NotSupportedException"/>.
    /// </summary>
    /// <remarks>
    /// This is <b>not a memory-quality mode</b>: nothing is extracted, normalized into third-person facts or filtered, so
    /// conclusions are copies of messages. It exists to exercise the pipeline end to end (queue, dedup, embeddings,
    /// commit) without network access or credentials.
    /// </remarks>
    public static NachosBuilder UseFakeAI(this NachosBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        // Before UseChatClient, whose empty default profile would otherwise be added first.
        builder.Services.PostConfigure<LlmOptions>(options =>
            options.Profiles.TryAdd(LlmOptions.DefaultProfileName, new ModelProfile { Model = "fake" }));
        builder.UseChatClient((_, _) => new FakeDeriverChatClient());
        builder.UseEmbeddingGenerator(_ => new DeterministicEmbeddingGenerator());
        return builder;
    }
}
