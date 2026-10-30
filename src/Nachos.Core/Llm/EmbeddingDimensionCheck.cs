using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nachos.Core.Llm;

/// <summary>
/// Embeds one probe string at startup and fails the host if the vector size is not the one the schema stores. A probe that
/// cannot complete (an outage, throttling, a role assignment that has not propagated yet) only logs a warning: a
/// configuration mistake is the dimension mismatch, not a service that is briefly unreachable.
/// </summary>
public sealed partial class EmbeddingDimensionCheck(
    IEmbeddingGenerator<string, Embedding<float>> generator, ILogger<EmbeddingDimensionCheck> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        GeneratedEmbeddings<Embedding<float>> embeddings;
        try
        {
            embeddings = await generator.GenerateAsync(["nachos embedding dimension probe"], cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogProbeFailed(logger, exception);
            return;
        }

        var actual = embeddings[0].Vector.Length;
        if (actual != EmbeddingOptions.RequiredDimensions)
        {
            throw new InvalidOperationException(
                $"The configured embedding model returned {actual} dimensions but the database stores {EmbeddingOptions.RequiredDimensions}. " +
                $"Use a {EmbeddingOptions.RequiredDimensions}-dimension model such as text-embedding-3-small, " +
                $"or set Embeddings:RequestDimensions to {EmbeddingOptions.RequiredDimensions} if the model supports the dimensions parameter.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The embedding dimension probe could not complete, so the vector size was not verified. Startup continues; " +
                  "check the embedding endpoint, deployment name and role assignment.")]
    private static partial void LogProbeFailed(ILogger logger, Exception exception);
}
