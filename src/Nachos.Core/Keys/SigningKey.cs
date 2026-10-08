namespace Nachos.Core.Keys;

/// <summary>An ordinal key ID and a UTF-8 signing secret of at least 32 bytes.</summary>
public sealed record SigningKey(string Kid, string Secret)
{
    public override string ToString() => nameof(SigningKey);
}
