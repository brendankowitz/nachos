namespace Nachos.Hosting;

/// <summary>
/// Your own Azure OpenAI resource, bound from <c>AzureOpenAI</c>. Nachos never creates the account; the identity running the host
/// needs the Cognitive Services OpenAI User role on it.
/// </summary>
public sealed class AzureOpenAIOptions
{
    /// <summary>The Azure OpenAI resource endpoint, for example <c>https://contoso.openai.azure.com/</c>.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Chat deployment used by profiles that do not name a <c>Model</c>.</summary>
    public string? ChatDeployment { get; set; }

    /// <summary>Embedding deployment; its model must produce 1536 dimensions (or support the <c>Embeddings:RequestDimensions</c> parameter).</summary>
    public string? EmbeddingDeployment { get; set; }
}
