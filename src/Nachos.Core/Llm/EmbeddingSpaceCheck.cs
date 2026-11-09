using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Domain;

namespace Nachos.Core.Llm;

/// <summary>
/// Fails the host at startup unless the configured embedding generator fits the database: its vectors must have the size
/// the schema stores, and its model must be the one the database is stamped with (<see cref="EmbeddingSpace"/>), because
/// vectors from two models are never comparable.
/// </summary>
/// <remarks>
/// <para>
/// Size: one probe string is embedded. A probe refused for a reason retrying cannot fix (a 400 for an unsupported setting,
/// a 404 for a missing deployment) fails startup; one that fails transiently (an outage, throttling) or for authorization
/// (a role assignment that has not propagated yet) only logs a warning, since the service may be briefly unreachable.
/// </para>
/// <para>
/// Model: the generator's model id (<see cref="EmbeddingModelName"/>) is compared with the stamp. An unstamped database is
/// stamped on first start when it stores no vector, or when every stored vector records the configured model; otherwise
/// the host fails with the reset command. Message chunk vectors record no model, so an unstamped database that has any
/// cannot be stamped automatically.
/// </para>
/// <para>
/// A host must start while its database is unreachable (readiness reports it), so a store failure during the model check
/// only logs a warning; the check is then repeated in the background (after 1 s, doubling to 30 s) until it completes,
/// and a mismatch found then stops the host (<paramref name="lifetime"/>; without one, it is only logged). Until that
/// recheck, a host could embed with an unchecked model.
/// </para>
/// </remarks>
public sealed partial class EmbeddingSpaceCheck(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    IServiceScopeFactory scopes,
    ILogger<EmbeddingSpaceCheck> logger,
    IHostApplicationLifetime? lifetime = null)
    : IHostedService, IDisposable
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _stopping = new();
    private Task _recheck = Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ProbeDimensionsAsync(cancellationToken).ConfigureAwait(false);
        string? mismatch;
        try
        {
            mismatch = await FindMismatchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogStoreUnavailable(logger, exception);
            _recheck = Task.Run(() => RecheckAsync(_stopping.Token), CancellationToken.None);
            return;
        }

        if (mismatch is not null)
        {
            throw new InvalidOperationException(mismatch);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _recheck.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _stopping.Dispose();

    /// <summary>The stamp the configured generator needs: its model id and that model's own vector size.</summary>
    public static EmbeddingSpace SpaceOf(string model) =>
        new(model, LocalEmbeddingModel.Find(model)?.Dimensions ?? VectorSpace.Dimensions);

    /// <summary>The startup failure for a database stamped with, or holding vectors of, another model.</summary>
    public static string MismatchMessage(string configured, string stamped) =>
        $"Embedding model '{configured}' does not match the database's '{stamped}'. " +
        $"Run 'nachos embeddings reset --model {configured}' to re-embed, or configure '{stamped}'.";

    private async Task RecheckAsync(CancellationToken ct)
    {
        var delay = FirstRetry;
        while (true)
        {
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                if (await FindMismatchAsync(ct).ConfigureAwait(false) is { } mismatch)
                {
                    LogMismatchStopping(logger, mismatch);
                    lifetime?.StopApplication();
                }
                else
                {
                    LogRechecked(logger);
                }

                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogRecheckFailed(logger, exception);
                delay = delay * 2 < MaxRetry ? delay * 2 : MaxRetry;
            }
        }
    }

    /// <summary>The mismatch message, or null once the database is stamped with the configured model (stamping it if allowed).</summary>
    private async Task<string?> FindMismatchAsync(CancellationToken ct)
    {
        var configured = SpaceOf(EmbeddingModelName.Of(generator));
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMemoryStore>().Embeddings;
        var stamp = await store.GetSpaceAsync(ct).ConfigureAwait(false);
        if (stamp is null)
        {
            var others = (await store.ListVectorModelsAsync(ct).ConfigureAwait(false))
                .Where(model => !string.Equals(model, configured.Model, StringComparison.Ordinal))
                .Select(model => model ?? "(not recorded)")
                .Order(StringComparer.Ordinal)
                .ToList();
            if (others.Count > 0)
            {
                return MismatchMessage(configured.Model, string.Join("', '", others));
            }

            stamp = await store.StampSpaceAsync(configured, ct).ConfigureAwait(false);
            if (stamp == configured)
            {
                LogStamped(logger, configured.Model);
            }
        }

        return string.Equals(stamp.Model, configured.Model, StringComparison.Ordinal)
            ? null
            : MismatchMessage(configured.Model, stamp.Model);
    }

    private async Task ProbeDimensionsAsync(CancellationToken cancellationToken)
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

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The embedding dimension probe could not complete, so the vector size was not verified. Startup continues; " +
                  "check the embedding endpoint, deployment name and role assignment.")]
    private static partial void LogProbeFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The embedding model could not be checked against the database's stamp because the store is unavailable. " +
                  "Startup continues and the check is retried in the background.")]
    private static partial void LogStoreUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The embedding model check is still waiting for the store.")]
    private static partial void LogRecheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "The embedding model matches the database's stamp.")]
    private static partial void LogRechecked(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical, Message = "{Mismatch} Stopping the host.")]
    private static partial void LogMismatchStopping(ILogger logger, string mismatch);

    [LoggerMessage(Level = LogLevel.Information, Message = "The database had no embedding model stamp; it is now stamped with '{Model}'.")]
    private static partial void LogStamped(ILogger logger, string model);
}
