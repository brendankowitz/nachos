using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nachos.Hosting;

/// <summary>
/// Makes the fake AI visible at startup. Chosen explicitly (<c>Nachos:Ai:Mode=Fake</c>) or by falling back in the
/// Development environment, it logs a warning; a fallback anywhere else fails the host, so a deployment that lost its
/// Azure OpenAI settings cannot quietly fill its database with fake conclusions and vectors.
/// </summary>
/// <remarks>A host without an <see cref="IHostEnvironment"/> is treated as not Development.</remarks>
internal sealed partial class FakeAiStartupCheck(
    FakeAiSelection selection, ILogger<FakeAiStartupCheck> logger, IHostEnvironment? environment = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (selection == FakeAiSelection.Fallback && environment?.IsDevelopment() != true)
        {
            throw new InvalidOperationException(
                $"No AI backend is configured and the environment is '{environment?.EnvironmentName ?? "unknown"}', not Development, " +
                "so the fake AI will not be used implicitly. Set AzureOpenAI:Endpoint (with AzureOpenAI:ChatDeployment and " +
                "AzureOpenAI:EmbeddingDeployment) to use Azure OpenAI, or set Nachos:Ai:Mode=Fake to run the fake AI on purpose.");
        }

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

/// <summary>How <c>UseConfiguredAI</c> came to select the fake AI.</summary>
internal enum FakeAiSelection
{
    /// <summary><c>Nachos:Ai:Mode</c> is <c>Fake</c>.</summary>
    Explicit,

    /// <summary>No Azure OpenAI endpoint is configured and no mode was set.</summary>
    Fallback,
}
