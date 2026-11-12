using System.Globalization;
using System.Text;
using Nachos.Abstractions.Stores;
using Nachos.Core.Search;
using Shouldly;

namespace Nachos.Core.Tests.Search;

public sealed class LexicalQueryTests
{
    [Theory]
    [InlineData("\"lemon\" AND (tart* OR NEAR~pie)", new[] { "lemon", "tart", "near", "pie" })]
    [InlineData("AND OR NOT", new[] { "and", "or", "not" })]
    [InlineData("FORMSOF(INFLECTIONAL, run)", new[] { "formsof", "inflectional", "run" })]
    [InlineData("isabout(cake weight(0.5))", new[] { "isabout", "cake", "weight", "0", "5" })]
    [InlineData("50% off [sale] _under_ 'quotes' \\slash; --comment", new[] { "50", "off", "sale", "under", "quotes", "slash", "comment" })]
    [InlineData("\u00DCn\u00EFc\u00F6d\u00E9 caf\u00E9 \u0416\u0423\u0420\u041D\u0410\u041B", new[] { "\u00FCn\u00EFc\u00F6d\u00E9", "caf\u00E9", "\u0436\u0443\u0440\u043D\u0430\u043B" })]
    [InlineData("\u6771\u4EAC\u30BF\u30EF\u30FC", new[] { "\u6771\u4EAC\u30BF\u30EF\u30FC" })]
    [InlineData("e\u0301clair", new[] { "e\u0301clair" })]
    [InlineData("lone \uD800 surrogate", new[] { "lone", "surrogate" })]
    public void HostileQuery_IsTermsOnly(string query, string[] expected)
    {
        var terms = LexicalQuery.Terms(query);

        terms.ShouldBe(expected);
        terms.ShouldAllBe(term => term.Length > 0 && term.EnumerateRunes().All(IsWordRune));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \t\n")]
    [InlineData("\"*\" ~ () & | ! - + , . ; :")]
    [InlineData("\uDC00\uD800")]
    public void NoWordTokens_Empty(string query)
    {
        LexicalQuery.Terms(query).ShouldBeEmpty();
    }

    [Fact]
    public void Stopwords_DroppedWhenOtherTermsRemain()
    {
        LexicalQuery.Terms("What did the user say about lemons?").ShouldBe(["user", "say", "lemons"]);

        // The cap counts the words that remain, wherever the stopwords are.
        var query = "the a an of " + string.Join(" and ", Enumerable.Range(1, 20).Select(i => $"w{i}"));
        LexicalQuery.Terms(query).ShouldBe([.. Enumerable.Range(1, 16).Select(i => $"w{i}")]);
    }

    [Theory]
    [InlineData("the", new[] { "the" })]
    [InlineData("What is it?", new[] { "what", "is", "it" })]
    [InlineData("THE the The", new[] { "the" })]
    public void OnlyStopwords_Kept(string query, string[] expected)
    {
        LexicalQuery.Terms(query).ShouldBe(expected);
    }

    [Fact]
    public void CapsAt16Terms()
    {
        var query = string.Join(' ', Enumerable.Range(1, 20).Select(i => $"w{i}"));

        LexicalQuery.Terms(query).ShouldBe([.. Enumerable.Range(1, 16).Select(i => $"w{i}")]);
    }

    [Fact]
    public void Deduplicates_CaseInsensitively_InFirstOccurrenceOrder()
    {
        var query = "Tea tea TEA coffee Tea " + string.Join(' ', Enumerable.Range(1, 15).Select(i => $"w{i}"));

        LexicalQuery.Terms(query).ShouldBe(["tea", "coffee", .. Enumerable.Range(1, 14).Select(i => $"w{i}")]);
    }

    [Fact]
    public void DropsTermsOverMaxLength()
    {
        var tooLong = new string('a', IMessageSearch.MaxTermLength + 1);
        var longest = new string('b', IMessageSearch.MaxTermLength);

        LexicalQuery.Terms($"{tooLong} tea").ShouldBe(["tea"]);
        LexicalQuery.Terms(longest).ShouldBe([longest]);
    }

    [Fact]
    public void DroppedTermsDoNotCountTowardsTheCap()
    {
        var tooLong = new string('a', IMessageSearch.MaxTermLength + 1);
        var query = string.Join(' ', Enumerable.Repeat(tooLong, 20).Concat(Enumerable.Range(1, 16).Select(i => $"w{i}")));

        LexicalQuery.Terms(query).Count.ShouldBe(16);
    }

    private static bool IsWordRune(Rune rune) =>
        Rune.IsLetterOrDigit(rune)
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
}
