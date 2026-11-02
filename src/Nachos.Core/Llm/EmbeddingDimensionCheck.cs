using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nachos.Abstractions.Domain;

namespace Nachos.Core.Llm;

/// <summary>
/// Embeds one probe string at startup and fails the host if the vector size is not the one the schema stores, or if the
/// probe is refused for a reason retrying cannot fix (a 400 for an unsupported setting, a 404 for a missing deployment).
/// A probe that fails transiently (an outage, throttling) or for authorization (a role assignment that has not propagated
/// yet) only logs a warning: the service may be briefly unreachable.
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
            if (!ProviderFailure.IsTransient(exception, cancellationToken) && !ProviderFailure.IsAuthorizationFailure(exception))
            {
                throw new InvalidOperationException(
                    "The embedding dimension probe was refused, so the embedding configuration cannot work. Check the " +
                    "Embeddings settings (deployment or model name, RequestDimensions) and the embedding endpoint.",
                    exception);
            }

            LogProbeFailed(logger, exception);
            return;
        }

        if (embeddings.Count != 1)
        {
            throw new InvalidOperationException(
                $"The configured embedding generator returned {embeddings.Count} embeddings for one probe input. " +
                "Check the Embeddings configuration.");
        }

        var actual = embeddings[0].Vector.Length;
        if (actual != VectorSpace.Dimensions)
        {
            throw new InvalidOperationException(
                $"The configured embedding model returned {actual} dimensions but the database stores {VectorSpace.Dimensions}. " +
                $"Use a {VectorSpace.Dimensions}-dimension model such as text-embedding-3-small, " +
                $"or set Embeddings:RequestDimensions to {VectorSpace.Dimensions} if the model supports the dimensions parameter.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The embedding dimension probe could not complete, so the vector size was not verified. Startup continues; " +
                  "check the embedding endpoint, deployment name and role assignment.")]
    private static partial void LogProbeFailed(ILogger logger, Exception exception);
}
