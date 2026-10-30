using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Nachos.DataLayer.SqlServer.Storage;

/// <summary>
/// <c>Conclusions.ContentHash</c>: the SHA-256 of the content's UTF-16 code units. Every write of <c>Content</c> stores it
/// too, and exact matches seek on it first, then confirm with
/// <c>Content = @content COLLATE Latin1_General_100_BIN2_UTF8</c>. The hash also tells apart contents that differ only by
/// trailing spaces, which <c>=</c> ignores even under a binary collation.
/// </summary>
internal static class ContentHash
{
    public static byte[] Of(string content) => SHA256.HashData(MemoryMarshal.AsBytes(content.AsSpan()));
}
