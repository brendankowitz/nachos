using Microsoft.ML.Tokenizers;

namespace Nachos.Core.Tokens;

public sealed class TiktokenTokenCounter : ITokenCounter
{
    private static readonly TiktokenTokenizer Tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public int Count(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Tokenizer.CountTokens(text);
    }
}
