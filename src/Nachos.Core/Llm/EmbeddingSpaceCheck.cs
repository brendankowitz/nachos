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
/// A host must start while its database is unreachable (readiness reports it), so a store that cannot be reached
/// (<see cref="IMemoryStore.IsUnavailableFailure"/>) only logs a warning. The <see cref="EmbeddingSpaceGate"/> is then closed,
/// so every embedding waits, and the check is repeated in the background (after 1 s, doubling to 5 s): a match opens the
/// gate; a mismatch, or any other failure, keeps it closed, sets exit code 1 and stops the host (<paramref name="lifetime"/>;
/// without one it is only logged). Any other store failure at startup (a missing table, a refused schema, a denied
/// permission) fails startup. The probe uses the generator behind the gate, so it never waits on it.
/// </para>
/// </remarks>
public sealed partial class EmbeddingSpaceCheck(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    IServiceScopeFactory scopes,
    EmbeddingSpaceGate gate,
    ILogger<EmbeddingSpaceCheck> logger,
    IHostApplicationLifetime? lifetime = null)
    : IHostedService, IDisposable
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _stopping = new();
    private Task _recheck = Task.CompletedTask;
    private bool _disposed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ProbeDimensionsAsync(cancellationToken).ConfigureAwait(false);
        var configured = SpaceOf(EmbeddingModelName.Of(generator));
        var (mismatch, unavailable) = await CheckAsync(configured, cancellationToken).ConfigureAwait(false);
        if (unavailable is not null)
        {
            gate.Close();
            LogStoreUnavailable(logger, unavailable);
            _recheck = Task.Run(() => RecheckAsync(configured, _stopping.Token), CancellationToken.None);
            return;
        }

        if (mismatch is not null)
        {
            throw new InvalidOperationException(mismatch);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // A host may dispose its services before stopping them (WebApplicationFactory does); the recheck is cancelled by then.
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        await _recheck.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, true))
        {
            return;
        }

        _stopping.Cancel();
        _stopping.Dispose();
    }

    /// <summary>The stamp the configured generator needs: its model id and that model's own vector size.</summary>
    /// <exception cref="ArgumentException">The model id is blank or longer than <see cref="EmbeddingSpace.MaxModelLength"/>.</exception>
    public static EmbeddingSpace SpaceOf(string model) =>
        new(model, LocalEmbeddingModel.Find(model)?.Dimensions ?? VectorSpace.Dimensions);

    /// <summary>The startup failure for a database stamped with, or holding vectors of, another model.</summary>
    public static string MismatchMessage(string configured, string stamped) =>
        $"Embedding model '{configured}' does not match the database's '{stamped}'. " +
        $"Run 'nachos embeddings reset --model {configured}' to re-embed, or configure '{stamped}'.";

    private async Task RecheckAsync(EmbeddingSpace configured, CancellationToken ct)
    {
        var delay = FirstRetry;
        while (true)
        {
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                var (mismatch, unavailable) = await CheckAsync(configured, ct).ConfigureAwait(false);
                if (unavailable is not null)
                {
                    LogRecheckFailed(logger, unavailable);
                    delay = delay * 2 < MaxRetry ? delay * 2 : MaxRetry;
                    continue;
                }

                if (mismatch is null)
                {
                    gate.Open();
                    LogRechecked(logger);
                    return;
                }

                Stop(mismatch, null);
                return;
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // Stopping: an in-flight store call may fail with something other than OperationCanceledException (a
                // provider's own "operation cancelled", or a disposed scope), which is not a verdict on the database.
                return;
            }
            catch (Exception exception)
            {
                Stop("The embedding model could not be checked against the database's stamp.", exception);
                return;
            }
        }
    }

    /// <summary>Keeps the gate closed and stops the host with exit code 1.</summary>
    private void Stop(string reason, Exception? exception)
    {
        LogStopping(logger, reason, exception);
        Environment.ExitCode = 1;
        lifetime?.StopApplication();
    }

    /// <summary>
    /// The mismatch message, or null once the database is stamped with the configured model (stamping it if allowed); or the
    /// failure, when the store could not be reached. Any other store failure is thrown.
    /// </summary>
    private async Task<(string? Mismatch, Exception? Unavailable)> CheckAsync(EmbeddingSpace configured, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var memory = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
        try
        {
            return (await FindMismatchAsync(memory.Embeddings, configured, ct).ConfigureAwait(false), null);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested && memory.IsUnavailableFailure(exception))
        {
            return (null, exception);
        }
    }

    private async Task<string?> FindMismatchAsync(IEmbeddingStore store, EmbeddingSpace configured, CancellationToken ct)
    {
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
            var ungated = generator.GetService<GatedEmbeddingGenerator>()?.Ungated ?? generator;
            embeddings = await ungated.GenerateAsync(["nachos embedding dimension probe"], cancellationToken: cancellationToken)
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
                  "Startup continues, embeddings are held back, and the check is retried in the background.")]
    private static partial void LogStoreUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The embedding model check is still waiting for the store; embeddings stay held back.")]
    private static partial void LogRecheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "The embedding model matches the database's stamp; embeddings are released.")]
    private static partial void LogRechecked(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical, Message = "{Reason} Embeddings stay held back; stopping the host.")]
    private static partial void LogStopping(ILogger logger, string reason, Exception? exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "The database had no embedding model stamp; it is now stamped with '{Model}'.")]
    private static partial void LogStamped(ILogger logger, string model);
}
