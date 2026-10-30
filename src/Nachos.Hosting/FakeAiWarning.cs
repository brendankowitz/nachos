using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nachos.Hosting;

/// <summary>Says at startup that the fake AI is active, so a deployment cannot run it unnoticed.</summary>
internal sealed partial class FakeAiWarning(ILogger<FakeAiWarning> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogFakeAiActive(logger);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Fake AI is active; conclusions are copies of messages. This is not a memory-quality mode. " +
                  "Set AzureOpenAI:Endpoint to use Azure OpenAI.")]
    private static partial void LogFakeAiActive(ILogger logger);
}
