using System.ClientModel.Primitives;
using Azure.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Nachos.Hosting;
using OpenAI;

namespace Microsoft.Extensions.DependencyInjection;

public static class NachosAzureOpenAIExtensions
{
    private const string TokenScope = "https://cognitiveservices.azure.com/.default";

    /// <summary>
    /// Uses Azure OpenAI for chat and embeddings, authenticating with <paramref name="credential"/> (managed identity or
    /// <c>DefaultAzureCredential</c>), never an API key. The OpenAI SDK talks to the resource's <c>/openai/v1/</c> endpoint.
    /// Configuration is read when the first client is requested, so a missing endpoint or deployment fails there with a clear message.
    /// </summary>
    public static NachosBuilder UseAzureOpenAI(
        this NachosBuilder builder, TokenCredential credential, Action<AzureOpenAIOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(configure);
        builder.Services.Configure(configure);
        builder.Services.AddSingleton(provider => CreateClient(credential, provider.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value));

        return builder
            .UseChatClient((provider, profile) =>
            {
                var options = provider.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;
                var deployment = profile.Model ?? options.ChatDeployment
                    ?? throw new InvalidOperationException(
                        "No Azure OpenAI chat deployment is configured. Set AzureOpenAI:ChatDeployment or Llm:Profiles:{name}:Model.");
                return provider.GetRequiredService<OpenAIClient>().GetChatClient(deployment).AsIChatClient();
            })
            .UseEmbeddingGenerator(provider =>
            {
                var options = provider.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;
                var deployment = options.EmbeddingDeployment
                    ?? throw new InvalidOperationException(
                        "No Azure OpenAI embedding deployment is configured. Set AzureOpenAI:EmbeddingDeployment.");
                return provider.GetRequiredService<OpenAIClient>().GetEmbeddingClient(deployment).AsIEmbeddingGenerator();
            });
    }

    private static OpenAIClient CreateClient(TokenCredential credential, AzureOpenAIOptions options)
    {
        var endpoint = options.Endpoint
            ?? throw new InvalidOperationException("No Azure OpenAI endpoint is configured. Set AzureOpenAI:Endpoint.");
        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(endpoint, "openai/v1/") };
#pragma warning disable OPENAI001 // The token-credential constructor is the supported Azure OpenAI path; it is marked experimental in the SDK.
        return new OpenAIClient(new BearerTokenPolicy(credential, TokenScope), clientOptions);
#pragma warning restore OPENAI001
    }
}
