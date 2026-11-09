using Nachos.DataLayer.SqlServer.Search;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

public sealed class FullTextConditionTests
{
    [Theory]
    [InlineData("\"")]
    [InlineData("*")]
    [InlineData("lemon*")]
    [InlineData("(")]
    [InlineData("~")]
    [InlineData("a\"b")]
    [InlineData("lemon_lime")]
    [InlineData("100%")]
    [InlineData("x') OR 1=1 --")]
    [InlineData("東京タワー!")]
    public void HostileTerm_FallsBackToLike(string hostile)
    {
        // A term that is not a plain word never reaches CONTAINSTABLE, whatever the other terms are.
        FullTextCondition.For([hostile]).ShouldBeNull();
        FullTextCondition.For(["lemon", hostile]).ShouldBeNull();
    }

    [Fact]
    public void PlainWords_BecomeQuotedPrefixTermsJoinedByAnd()
    {
        // Operator words are only words inside quotes.
        FullTextCondition.For(["lemon", "NEAR", "AND", "東京", "café", "42"])
            .ShouldBe("\"lemon*\" AND \"NEAR*\" AND \"AND*\" AND \"東京*\" AND \"café*\" AND \"42*\"");
        FullTextCondition.For([]).ShouldBeNull();
    }

    [Theory]
    [InlineData("cafe\u0301")]
    [InlineData("हिन्दी")]
    [InlineData("a\u20DD")]
    public void CombiningMarks_ArePartOfAWord(string term) =>
        FullTextCondition.For([term]).ShouldBe($"\"{term}*\"");

    [Fact]
    public void LongCondition_FallsBackToLike()
    {
        var longest = new string('a', FullTextCondition.MaxLength - 3);
        FullTextCondition.For([longest]).ShouldNotBeNull().Length.ShouldBe(FullTextCondition.MaxLength);
        FullTextCondition.For([longest + "a"]).ShouldBeNull();
        FullTextCondition.For([new string('a', 2000), new string('b', 2000)]).ShouldBeNull();
    }
}

public sealed class LikePatternTests
{
    [Theory]
    [InlineData("100%", @"%100\%%")]
    [InlineData("a_b", @"%a\_b%")]
    [InlineData("[x]", @"%\[x]%")]
    [InlineData(@"c:\temp", @"%c:\\temp%")]
    [InlineData("plain", "%plain%")]
    public void Pattern_EscapesEveryWildcard(string term, string pattern) =>
        LikePattern.For(term).ShouldBe(pattern);
}
