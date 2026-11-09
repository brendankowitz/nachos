using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nachos.Hosting;

/// <summary>
/// Warns at startup when no embedding generator is registered: conclusions created through the API then stay unembedded
/// (the reconciler runs in the worker), semantic queries fail, and message search is lexical only.
/// </summary>
internal sealed partial class MissingEmbeddingGeneratorWarning(
    IEnumerable<IEmbeddingGenerator<string, Embedding<float>>> generators,
    ILogger<MissingEmbeddingGeneratorWarning> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!generators.Any())
        {
            LogNoEmbeddingGenerator(logger);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No embedding generator is registered. Semantic queries (conclusion query, representation search) will fail, " +
                  "message search will be lexical only, and API-created conclusions will not be embedded. Call UseAzureOpenAI, " +
                  "UseEmbeddingGenerator or UseFakeAI.")]
    private static partial void LogNoEmbeddingGenerator(ILogger logger);
}
