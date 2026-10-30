namespace Nachos.Core.Llm;

/// <summary>One named chat-model profile under <c>Llm:Profiles:{name}</c>.</summary>
public sealed class ModelProfile
{
    /// <summary>Provider model or deployment name; when null the provider's default chat deployment is used.</summary>
    public string? Model { get; set; }

    /// <summary>Upper bound on generated tokens per request; null leaves the provider's default.</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>Sampling temperature; null leaves the provider's default.</summary>
    public float? Temperature { get; set; }

    /// <summary>Name of the profile tried next when this one fails with a retryable error.</summary>
    public string? Fallback { get; set; }
}
