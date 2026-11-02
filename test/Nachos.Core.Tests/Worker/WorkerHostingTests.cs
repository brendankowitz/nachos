using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nachos.Core.Deriver;
using Nachos.Core.Worker;
using Shouldly;

namespace Nachos.Core.Tests.Worker;

public sealed class WorkerHostingTests
{
    private static ServiceProvider Build(Dictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build());
        services.AddNachos(builder => builder.UseInMemory().UseFakeAI().AddNachosWorker().AddNachosWorker());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Fact]
    public void AddNachosWorker_RegistersTheLoopsAndTheDeriverOnce()
    {
        using var provider = Build(new() { ["Worker:MaxConcurrency"] = "7", ["Reconciler:TombstoneRetention"] = "01:00:00" });

        var hosted = provider.GetServices<IHostedService>().ToList();
        hosted.OfType<QueueConsumerService>().ShouldHaveSingleItem();
        hosted.OfType<ReconcilerService>().ShouldHaveSingleItem();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetServices<IWorkHandler>().ShouldHaveSingleItem().ShouldBeOfType<DeriverService>();
        provider.GetRequiredService<IOptions<WorkerOptions>>().Value.MaxConcurrency.ShouldBe(7);
        provider.GetRequiredService<IOptions<ReconcilerOptions>>().Value.TombstoneRetention.ShouldBe(TimeSpan.FromHours(1));
    }

    [Theory]
    [InlineData("Worker:MaxConcurrency", "0", "Worker:MaxConcurrency")]
    [InlineData("Worker:MaxIdleDelay", "00:00:00.5", "Worker:IdleDelay")]
    [InlineData("Worker:RetryDelay", "00:00:00", "Worker:RetryDelay")]
    [InlineData("Worker:TransientFailuresBeforeError", "0", "Worker:TransientFailuresBeforeError")]
    [InlineData("Reconciler:EmbeddingBatchSize", "0", "Reconciler:EmbeddingBatchSize")]
    [InlineData("Reconciler:LeaseDuration", "00:00:00", "Reconciler:LeaseDuration")]
    [InlineData("Reconciler:FailedEmbeddingTicksBeforeError", "0", "Reconciler:FailedEmbeddingTicksBeforeError")]
    public void InvalidOptions_FailWithTheirKey(string key, string value, string expected)
    {
        using var provider = Build(new() { [key] = value });

        var failure = Should.Throw<OptionsValidationException>(() =>
        {
            _ = provider.GetRequiredService<IOptions<WorkerOptions>>().Value;
            _ = provider.GetRequiredService<IOptions<ReconcilerOptions>>().Value;
        });
        failure.Message.ShouldContain(expected);
    }
}
