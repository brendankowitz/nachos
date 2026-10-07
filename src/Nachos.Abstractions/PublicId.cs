using System.Security.Cryptography;

namespace Nachos.Abstractions;

/// <summary>Generates the opaque public ids of messages.</summary>
public static class PublicId
{
    public const int Length = 21;

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";

    /// <summary>A new 21-character id over <c>A-Za-z0-9_-</c>, drawn without modulo bias from a CSPRNG.</summary>
    public static string New() => RandomNumberGenerator.GetString(Alphabet, Length);
}