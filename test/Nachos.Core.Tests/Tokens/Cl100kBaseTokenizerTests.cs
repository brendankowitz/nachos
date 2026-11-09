using Microsoft.ML.Tokenizers;
using Nachos.Core.Tokens;
using Shouldly;

namespace Nachos.Core.Tests.Tokens;

public sealed class Cl100kBaseTokenizerTests
{
    private static readonly string[] Corpus =
    [
        "Hello, world!",
        "I'm sure they'll say it's fine; YOU'RE right, We'VE seen It'S, DON'T worry.",
        "line one\r\nline two\r\n\r\n  indented\tline\n\nend",
        "Order 1234567 cost 89.99 on 2026-11-09, ref #000123.",
        "日本語のテキストと中文文本，한국어 텍스트",
        "emoji 🙂🎉👨‍👩‍👧 and flags 🇳🇿",
        "Café naïve résumé Zürich Ångström",
        "wait... what?!?! --- *** ((())) [[]] {{}} ;;;; ////",
        "   leading and trailing spaces   ",
        "ALLCAPS camelCaseWord snake_case_word kebab-case-word",
        "",
    ];

    [Fact]
    public void MatchesStockTiktoken_OnFixedCorpus()
    {
        var stock = TiktokenTokenizer.CreateForEncoding("cl100k_base");

        foreach (var text in Corpus)
        {
            Cl100kBaseTokenizer.Instance.EncodeToIds(text).ShouldBe(stock.EncodeToIds(text), $"for: {text}");
        }
    }
}
