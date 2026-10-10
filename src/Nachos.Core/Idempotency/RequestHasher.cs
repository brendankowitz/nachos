using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Nachos.Core.Idempotency;

public static class RequestHasher
{
    /// <summary>
    /// Hashes UTF-8 method and route, an ordinally sorted route-value map, and canonical JSON bytes.
    /// Components are length-prefixed (32-bit big-endian); the map also has a 32-bit entry count.
    /// One trailing route slash is an alias. Resolved route values retain their case.
    /// </summary>
    public static string Hash(
        string method,
        string routeTemplate,
        IReadOnlyDictionary<string, string> routeValues,
        ReadOnlySpan<byte> canonicalBody)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Encoding.UTF8.GetBytes(method));
        var canonicalRoute = routeTemplate.Length > 1 && routeTemplate.EndsWith('/')
            ? routeTemplate[..^1]
            : routeTemplate;
        Append(hash, Encoding.UTF8.GetBytes(canonicalRoute));
        Span<byte> count = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(count, routeValues.Count);
        hash.AppendData(count);
        foreach (var (name, value) in routeValues.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Append(hash, Encoding.UTF8.GetBytes(name));
            Append(hash, Encoding.UTF8.GetBytes(value));
        }

        Append(hash, canonicalBody);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }
}
