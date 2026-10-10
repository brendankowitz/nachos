using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

namespace Nachos.Core.Tokens;

public sealed class TiktokenTokenCounter : ITokenCounter
{
    // Pattern and vocabulary provenance: research/token-feasibility and THIRD-PARTY-NOTICES.md.
    private const string O200kPattern =
        @"[^\r\n\p{L}\p{N}]?[\p{Lu}\p{Lt}\p{Lm}\p{Lo}\p{M}]*[\p{Ll}\p{Lm}\p{Lo}\p{M}]+(?i:'s|'t|'re|'ve|'m|'ll|'d)?" +
        @"|[^\r\n\p{L}\p{N}]?[\p{Lu}\p{Lt}\p{Lm}\p{Lo}\p{M}]+[\p{Ll}\p{Lm}\p{Lo}\p{M}]*(?i:'s|'t|'re|'ve|'m|'ll|'d)?" +
        @"|\p{N}{1,3}| ?[^\s\p{L}\p{N}]+[\r\n/]*|\s*[\r\n]+|\s+(?!\S)|\s+";

    private const string VocabularyResource = "o200k_base.tiktoken.deflate";
    private const string VocabularySha256 = "88B2A54DCEDC68D39B1AF4B8DC744ADBA8ECA01310B7D07A687F1ADD3BE75524";

    private static readonly TiktokenTokenizer Tokenizer = CreateTokenizer();

    public int Count(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Tokenizer.CountTokens(text);
    }

    private static TiktokenTokenizer CreateTokenizer()
    {
        // The embedded resource name/format is not a public contract; reject changed bytes, never fall back.
        using var resource = Assembly.Load("Microsoft.ML.Tokenizers.Data.O200kBase")
            .GetManifestResourceStream(VocabularyResource)
            ?? throw new InvalidOperationException("The pinned o200k_base vocabulary resource is missing.");
        if (Convert.ToHexString(SHA256.HashData(resource)) != VocabularySha256)
        {
            throw new InvalidOperationException("The embedded o200k_base vocabulary does not match the pinned SHA-256.");
        }

        resource.Position = 0;
        using var vocabulary = new DeflateStream(resource, CompressionMode.Decompress);
        // Ordinary IDs alone are insufficient: special-aware pretokens also alter adjacent spellings.
        var preTokenizer = new RegexPreTokenizer(
            new Regex(O200kPattern, RegexOptions.CultureInvariant), specialTokens: null);
        return TiktokenTokenizer.Create(vocabulary, preTokenizer, normalizer: null, specialTokens: null);
    }
}
