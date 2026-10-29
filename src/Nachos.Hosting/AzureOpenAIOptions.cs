namespace Nachos.Hosting;

public sealed class AzureOpenAIOptions
{
    /// <summary>The Azure OpenAI resource endpoint, for example <c>https://contoso.openai.azure.com/</c>.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Chat deployment used by profiles that do not name a <c>Model</c>.</summary>
    public string? ChatDeployment { get; set; }

    public string? EmbeddingDeployment { get; set; }
}
