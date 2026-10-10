namespace Nachos.Core.Keys;

/// <summary>A bindable key entry; the issuer requires an ordinal ID and a UTF-8 secret of at least 32 bytes.</summary>
public sealed record SigningKey(string? Kid, string? Secret)
{
    // Preserve incomplete bound entries so issuer validation rejects the ring instead of silently rotating it.
    public SigningKey() : this(null, null) { }

    public override string ToString() => nameof(SigningKey);
}
