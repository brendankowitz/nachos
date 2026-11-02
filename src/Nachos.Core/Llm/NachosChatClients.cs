using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Nachos.Core.Llm;

/// <summary>
/// Resolves the chat pipeline for an agent: OpenTelemetry, then span tagging, then the fallback chain of its profile,
/// then the provider client. Pipelines are built once per profile and live for the lifetime of the host.
/// </summary>
public sealed class NachosChatClients : IDisposable
{
    public const string TelemetrySourceName = "Nachos.Llm";

    private readonly LlmOptions _options;
    private readonly Func<ModelProfile, IChatClient> _createProviderClient;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IChatClient> _pipelines = new(StringComparer.OrdinalIgnoreCase);

    public NachosChatClients(LlmOptions options, Func<ModelProfile, IChatClient> createProviderClient, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(createProviderClient);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _createProviderClient = createProviderClient;
        _logger = logger;
    }

    /// <summary>Returns the client for <paramref name="agent"/>, using <c>Llm:Agents:{agent}</c> or the default profile.</summary>
    public IChatClient ForAgent(string agent)
    {
        ArgumentException.ThrowIfNullOrEmpty(agent);
        var profileName = _options.Agents.TryGetValue(agent, out var configured)
            ? configured
            : LlmOptions.DefaultProfileName;
        lock (_gate)
        {
            if (!_pipelines.TryGetValue(profileName, out var pipeline))
            {
                // Only successes are cached, so a transient failure while building the pipeline is retried on the next call.
                pipeline = BuildPipeline(profileName);
                _pipelines[profileName] = pipeline;
            }

            return pipeline;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var pipeline in _pipelines.Values)
            {
                pipeline.Dispose();
            }

            _pipelines.Clear();
        }
    }

    private IChatClient BuildPipeline(string profileName)
    {
        var chain = ResolveChain(profileName);
        var created = new List<IChatClient>();
        try
        {
            foreach (var (_, profile) in chain)
            {
                created.Add(CreateProfileClient(profile));
            }

            IChatClient client = created.Count == 1
                ? created[0]
                : new NachosFallbackChatClient(created[0], created.Skip(1).ToList(), chain.Select(link => link.Name).ToList(), _logger);
            return new ChatClientBuilder(client)
                .UseOpenTelemetry(sourceName: TelemetrySourceName)
                .Use(inner => new AgentTagChatClient(inner))
                .Build();
        }
        catch
        {
            foreach (var client in created)
            {
                client.Dispose();
            }

            throw;
        }
    }

    private List<(string Name, ModelProfile Profile)> ResolveChain(string profileName)
    {
        var chain = new List<(string, ModelProfile)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? next = profileName;
        while (next is not null)
        {
            if (!visited.Add(next))
            {
                throw new InvalidOperationException(
                    $"Llm profile '{next}' appears twice in the fallback chain starting at '{profileName}'. Remove the cycle from Llm:Profiles.");
            }

            if (!_options.Profiles.TryGetValue(next, out var profile))
            {
                throw new InvalidOperationException(
                    $"Llm profile '{next}' is not configured. Add Llm:Profiles:{next} or point Llm:Agents at an existing profile.");
            }

            chain.Add((next, profile));
            next = profile.Fallback;
        }

        return chain;
    }

    private IChatClient CreateProfileClient(ModelProfile profile) =>
        new ChatClientBuilder(_createProviderClient(profile))
            .ConfigureOptions(options =>
            {
                options.MaxOutputTokens ??= profile.MaxOutputTokens;
                options.Temperature ??= profile.Temperature;
            })
            .Build();
}
