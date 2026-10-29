using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

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
    private readonly ConcurrentDictionary<string, Lazy<IChatClient>> _pipelines = new(StringComparer.OrdinalIgnoreCase);

    public NachosChatClients(LlmOptions options, Func<ModelProfile, IChatClient> createProviderClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(createProviderClient);
        _options = options;
        _createProviderClient = createProviderClient;
    }

    /// <summary>Returns the client for <paramref name="agent"/>, using <c>Llm:Agents:{agent}</c> or the default profile.</summary>
    public IChatClient ForAgent(string agent)
    {
        ArgumentException.ThrowIfNullOrEmpty(agent);
        var profileName = _options.Agents.TryGetValue(agent, out var configured)
            ? configured
            : LlmOptions.DefaultProfileName;
        return _pipelines.GetOrAdd(profileName, name => new Lazy<IChatClient>(() => BuildPipeline(name))).Value;
    }

    public void Dispose()
    {
        foreach (var pipeline in _pipelines.Values)
        {
            if (pipeline.IsValueCreated)
            {
                pipeline.Value.Dispose();
            }
        }
    }

    private IChatClient BuildPipeline(string profileName)
    {
        var chain = ResolveChain(profileName);
        var primary = CreateProfileClient(chain[0]);
        var fallbacks = chain.Skip(1).Select(CreateProfileClient).ToList();
        IChatClient client = fallbacks.Count == 0 ? primary : new NachosFallbackChatClient(primary, fallbacks);
        return new ChatClientBuilder(client)
            .UseOpenTelemetry(sourceName: TelemetrySourceName)
            .Use(inner => new AgentTagChatClient(inner))
            .Build();
    }

    private List<ModelProfile> ResolveChain(string profileName)
    {
        var chain = new List<ModelProfile>();
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

            chain.Add(profile);
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
