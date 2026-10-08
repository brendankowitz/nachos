using Microsoft.Extensions.Options;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Core.Configuration;
using Nachos.Core.Tokens;
using Nachos.Core.Validation;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class TokenCounterTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("hello world", 2)]
    [InlineData("你好世界", 2)]
    [InlineData("お誕生日おめでとう", 8)]
    public void KnownStrings_UseO200kBase(string text, int expected)
    {
        var counter = new TiktokenTokenCounter();

        counter.Count(text).ShouldBe(expected);
    }

    [Fact]
    public void NullIsNotSilentlyCountedAsEmptyContent()
    {
        Should.Throw<ArgumentNullException>(() => new TiktokenTokenCounter().Count(null!));
    }

    [Theory]
    [InlineData(2000, false)]
    [InlineData(2001, true)]
    public void RealTokenCounts_EnforceTheInstructionBoundary(int tokens, bool reject)
    {
        var counter = new TiktokenTokenCounter();
        var instructions = string.Join(" ", Enumerable.Repeat("hello", tokens));
        var validator = new RequestValidator(Options.Create(new NachosOptions()), counter);
        var message = new MessageCreate("message", "peer",
            Configuration: new(new(CustomInstructions: instructions)));

        counter.Count(instructions).ShouldBe(tokens);
        if (reject)
        {
            Should.Throw<NachosValidationException>(() => validator.ValidateMessages([message]));
        }
        else
        {
            Should.NotThrow(() => validator.ValidateMessages([message]));
        }
    }

    [Fact]
    public async Task SharedTokenizer_CountsDeterministicallyUnderConcurrency()
    {
        var counter = new TiktokenTokenCounter();
        var results = await Task.WhenAll(Enumerable.Range(0, 128).Select(index =>
            Task.Run(() => index % 2 == 0 ? counter.Count("hello world") : counter.Count("お誕生日おめでとう"))));

        for (var index = 0; index < results.Length; index++)
        {
            results[index].ShouldBe(index % 2 == 0 ? 2 : 8);
        }
    }
}
