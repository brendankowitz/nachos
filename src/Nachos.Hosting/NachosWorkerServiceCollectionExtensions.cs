using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Nachos.Core.Worker;
using Nachos.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

public static class NachosWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Runs the background workers in this host: the <see cref="QueueConsumerService"/> (with the Deriver, see
    /// <see cref="NachosDeriverBuilderExtensions.AddDeriver"/>) and the <see cref="ReconcilerService"/>. Options bind from
    /// <c>Worker</c> and <c>Reconciler</c> and are validated at host start.
    /// </summary>
    /// <remarks>
    /// The Deriver needs a chat client and an embedding generator (<c>UseChatClient</c> and <c>UseEmbeddingGenerator</c>,
    /// <c>UseAzureOpenAI</c>, or <c>UseFakeAI</c>). The in-memory provider is only visible to its own process, so with it
    /// the workers must run inside the API host.
    /// </remarks>
    public static NachosBuilder AddNachosWorker(this NachosBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddDeriver();
        var services = builder.Services;
        services.AddOptions<WorkerOptions>()
            .BindConfiguration("Worker")
            .Validate(o => o.MaxConcurrency >= 1, "Worker:MaxConcurrency must be at least 1.")
            .Validate(
                o => o.IdleDelay > TimeSpan.Zero && o.MaxIdleDelay >= o.IdleDelay,
                "Worker:IdleDelay must be positive and Worker:MaxIdleDelay at least Worker:IdleDelay.")
            .Validate(
                o => o.RetryDelay >= TimeSpan.Zero && o.MaxRetryDelay >= o.RetryDelay,
                "Worker:RetryDelay must not be negative and Worker:MaxRetryDelay must be at least Worker:RetryDelay.")
            .ValidateOnStart();
        services.AddOptions<ReconcilerOptions>()
            .BindConfiguration("Reconciler")
            .Validate(
                o => o.Interval > TimeSpan.Zero && o.MaxInterval >= o.Interval,
                "Reconciler:Interval must be positive and Reconciler:MaxInterval at least Reconciler:Interval.")
            .Validate(o => o.LeaseDuration > TimeSpan.Zero, "Reconciler:LeaseDuration must be positive.")
            .Validate(
                o => o.TombstoneRetention >= TimeSpan.Zero && o.CompletedRetention >= TimeSpan.Zero,
                "Reconciler:TombstoneRetention and Reconciler:CompletedRetention must not be negative.")
            .Validate(
                o => o.EmbeddingBatchSize >= 1 && o.EmbeddingMaxAttempts >= 1,
                "Reconciler:EmbeddingBatchSize and Reconciler:EmbeddingMaxAttempts must be at least 1.")
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, QueueConsumerService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ReconcilerService>());
        return builder;
    }
}
