using System.Security.Cryptography;

namespace Nachos.Core.Llm;

/// <summary>One pinned file of a <see cref="LocalEmbeddingModel"/>.</summary>
/// <param name="Name">The file's name inside the model's folder.</param>
/// <param name="Source">The HTTPS download URL, at a fixed revision.</param>
/// <param name="Sha256">The file's SHA-256, lower-case hex.</param>
public sealed record LocalModelFile(string Name, Uri Source, string Sha256)
{
    /// <summary>The lower-case hex SHA-256 of the file at <paramref name="path"/>.</summary>
    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Whether the file at <paramref name="path"/> exists and has the pinned <see cref="Sha256"/>.</summary>
    public bool IsAt(string path) => File.Exists(path) && string.Equals(Hash(path), Sha256, StringComparison.Ordinal);
}
