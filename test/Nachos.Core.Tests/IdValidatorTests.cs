using Nachos.Abstractions;
using Nachos.Core.Validation;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class IdValidatorTests
{
    [Theory]
    [InlineData("a-Z_9")]
    [InlineData("Alice")]
    [InlineData("alice")]
    public void Accepts_AsciiNamesWithoutChangingCase(string id)
    {
        Should.NotThrow(() => IdValidator.Validate(id, nameof(id)));
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("é")]
    [InlineData("a\n")]
    [InlineData("a.b")]
    [InlineData("a/b")]
    public void Rejects_CharactersOutsideThePublicAlphabet(string id)
    {
        var error = Should.Throw<NachosValidationException>(() => IdValidator.Validate(id, "peer_id"));

        error.Detail.ShouldContain("peer_id");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(513)]
    public void Rejects_LengthOutsideOneTo512(int length)
    {
        Should.Throw<NachosValidationException>(() => IdValidator.Validate(new string('a', length), "session_id"));
    }

    [Fact]
    public void Accepts_MaximumLength()
    {
        Should.NotThrow(() => IdValidator.Validate(new string('A', 512), "workspace_id"));
    }

    [Fact]
    public void Rejects_NullAtThePublicBoundary()
    {
        Should.Throw<NachosValidationException>(() => IdValidator.Validate(null!, "id"));
    }
}
