namespace Nachos.Core.Keys;

public sealed class SigningKeyOptions
{
    /// <summary>First key signs; every configured key validates. An empty ring disables issuance.</summary>
    public IReadOnlyList<SigningKey> Keys { get; set; } = [];
}
