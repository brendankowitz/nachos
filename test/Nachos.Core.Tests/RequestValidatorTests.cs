using Microsoft.Extensions.Options;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Core.Configuration;
using Nachos.Core.Tokens;
using Nachos.Core.Validation;
using NSubstitute;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class RequestValidatorTests
{
    private static RequestValidator CreateValidator() =>
        new(Options.Create(new NachosOptions()), Substitute.For<ITokenCounter>());

    [Fact]
    public void Messages_AcceptEmptyContentAndBackdating()
    {
        var validator = new RequestValidator(Options.Create(new NachosOptions()), Substitute.For<ITokenCounter>());
        var message = new MessageCreate("", "a-Z_9", CreatedAt: DateTimeOffset.UnixEpoch);

        Should.NotThrow(() => validator.ValidateMessages([message]));
    }

    [Fact]
    public void Messages_RejectReasoningInstructionsAboveTheDeploymentTokenBudget()
    {
        var counter = Substitute.For<ITokenCounter>();
        counter.Count("instructions").Returns(2001);
        var validator = new RequestValidator(Options.Create(new NachosOptions()), counter);
        var message = new MessageCreate("hello", "peer",
            Configuration: new MessageConfiguration(new ReasoningConfiguration(CustomInstructions: "instructions")));

        Should.Throw<NachosValidationException>(() => validator.ValidateMessages([message]));
    }

    [Theory]
    [InlineData(0, "too_short")]
    [InlineData(101, "too_long")]
    public void Messages_RejectBatchOutsideOneTo100(int count, string type)
    {
        var messages = Enumerable.Repeat(new MessageCreate("hello", "peer"), count).ToArray();

        var error = Should.Throw<RequestValidationException>(() => CreateValidator().ValidateMessages(messages));

        error.Errors.ShouldHaveSingleItem().Loc.ShouldBe(new object[] { "body", "messages" });
        error.Errors[0].Type.ShouldBe(type);
    }

    [Fact]
    public void Messages_RejectNullBatchWithWireValidationError()
    {
        var error = Should.Throw<RequestValidationException>(() => CreateValidator().ValidateMessages(null!));

        error.Errors.ShouldHaveSingleItem().Loc.ShouldBe(new object[] { "body", "messages" });
    }

    [Fact]
    public void Messages_RejectNullEntryWithItsArrayIndex()
    {
        var error = Should.Throw<RequestValidationException>(() =>
            CreateValidator().ValidateMessages([new("first", "peer"), null!]));

        error.Errors.ShouldHaveSingleItem().Loc.ShouldBe(new object[] { "body", "messages", 1 });
    }

    [Fact]
    public void Messages_RejectNullContentWithItsFieldLocation()
    {
        var error = Should.Throw<RequestValidationException>(() =>
            CreateValidator().ValidateMessages([new(null!, "peer")]));

        error.Errors.ShouldHaveSingleItem().Loc.ShouldBe(new object[] { "body", "messages", 0, "content" });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Messages_RejectOver25000UnicodeCharacters(bool supplementaryCharacters)
    {
        var content = supplementaryCharacters
            ? string.Concat(Enumerable.Repeat("😀", 25001))
            : new string('x', 25001);

        var error = Should.Throw<RequestValidationException>(() =>
            CreateValidator().ValidateMessages([new(content, "peer")]));

        error.Errors.ShouldHaveSingleItem().Loc.ShouldBe(new object[] { "body", "messages", 0, "content" });
        error.Errors[0].Type.ShouldBe("string_too_long");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Messages_Accept100EntriesAtTheUnicodeCharacterLimit(bool supplementaryCharacters)
    {
        var content = supplementaryCharacters
            ? string.Concat(Enumerable.Repeat("😀", 25000))
            : new string('x', 25000);

        Should.NotThrow(() => CreateValidator().ValidateMessages(
            Enumerable.Repeat(new MessageCreate(content, "a-Z_9"), 100).ToArray()));
    }

    [Fact]
    public void Messages_ValidateEverySenderBeforeAnyStoreCanBeCalled()
    {
        Should.Throw<NachosValidationException>(() =>
            CreateValidator().ValidateMessages([new("valid", "peer"), new("invalid", "a b")]));
    }

    [Fact]
    public void Messages_AllowInstructionsExactlyAtTheTokenLimit()
    {
        var counter = Substitute.For<ITokenCounter>();
        counter.Count("instructions").Returns(2000);
        var validator = new RequestValidator(Options.Create(new NachosOptions()), counter);

        Should.NotThrow(() => validator.ValidateMessages(
            [new("hello", "peer", Configuration: new(new(CustomInstructions: "instructions")))]));
    }
}
