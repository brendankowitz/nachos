using Nachos.DataLayer.SqlServer.Search;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

public sealed class LexicalQueryTests
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
    public void HostileQuery_IsTermsOnly(string hostile)
    {
        // A term that is not a plain word never reaches CONTAINSTABLE, whatever the other terms are.
        LexicalQuery.ToFullTextCondition([hostile]).ShouldBeNull();
        LexicalQuery.ToFullTextCondition(["lemon", hostile]).ShouldBeNull();
    }

    [Fact]
    public void PlainWords_BecomeQuotedTermsJoinedByAnd()
    {
        // Operator words are only words inside quotes.
        LexicalQuery.ToFullTextCondition(["lemon", "NEAR", "AND", "東京", "café", "42"])
            .ShouldBe("\"lemon\" AND \"NEAR\" AND \"AND\" AND \"東京\" AND \"café\" AND \"42\"");
        LexicalQuery.ToFullTextCondition([]).ShouldBeNull();
    }

    [Theory]
    [InlineData("100%", @"%100\%%")]
    [InlineData("a_b", @"%a\_b%")]
    [InlineData("[x]", @"%\[x]%")]
    [InlineData(@"c:\temp", @"%c:\\temp%")]
    [InlineData("plain", "%plain%")]
    public void LikePattern_EscapesEveryWildcard(string term, string pattern) =>
        LexicalQuery.ToLikePattern(term).ShouldBe(pattern);
}
