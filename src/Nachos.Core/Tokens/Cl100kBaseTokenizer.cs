using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

namespace Nachos.Core.Tokens;

/// <summary>The <c>cl100k_base</c> tokenizer of OpenAI's text-embedding-3 and ada-002 models, for sizing message chunks.</summary>
internal static class Cl100kBaseTokenizer
{
    // Pattern and vocabulary provenance: THIRD-PARTY-NOTICES.md.
    private const string Pattern =
        @"'(?i:[sdmt]|ll|ve|re)|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}{1,3}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+";

    private const string VocabularyResource = "cl100k_base.tiktoken.deflate";
    private const string VocabularySha256 = "05DD7477A7FEEDA8B71EB826B7E4C702E5DF6F44C35D6CE4D1EF3F7BFC8A0F36";

    private static readonly Lazy<TiktokenTokenizer> Shared = new(Create);

    public static TiktokenTokenizer Instance => Shared.Value;

    private static TiktokenTokenizer Create()
    {
        using var resource = Assembly.Load("Microsoft.ML.Tokenizers.Data.Cl100kBase")
            .GetManifestResourceStream(VocabularyResource);
        return Create(resource);
    }

    // The caller owns the compressed stream, positioned at its start; the tokenizer retains no stream.
    internal static TiktokenTokenizer Create(Stream? resource)
    {
        // The embedded resource name/format is not a public contract; reject changed bytes, never fall back.
        if (resource is null)
        {
            throw new InvalidOperationException("The pinned cl100k_base vocabulary resource is missing.");
        }

        if (Convert.ToHexString(SHA256.HashData(resource)) != VocabularySha256)
        {
            throw new InvalidOperationException("The embedded cl100k_base vocabulary does not match the pinned SHA-256.");
        }

        resource.Position = 0;
        using var vocabulary = new DeflateStream(resource, CompressionMode.Decompress, leaveOpen: true);
        // No special tokens: message text such as "<|endoftext|>" is ordinary text to size, not a control token.
        var preTokenizer = new RegexPreTokenizer(new Regex(Pattern, RegexOptions.CultureInvariant), specialTokens: null);
        return TiktokenTokenizer.Create(vocabulary, preTokenizer, normalizer: null, specialTokens: null);
    }
}
