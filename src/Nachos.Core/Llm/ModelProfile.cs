namespace Nachos.Core.Llm;

public sealed class ModelProfile
{
    /// <summary>Provider model or deployment name; when null the provider's default chat deployment is used.</summary>
    public string? Model { get; set; }

    public int? MaxOutputTokens { get; set; }

    public float? Temperature { get; set; }

    /// <summary>Name of the profile tried next when this one fails with a retryable error.</summary>
    public string? Fallback { get; set; }
}
