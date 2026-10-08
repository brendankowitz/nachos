using Shouldly;

namespace Nachos.Abstractions.Tests;

public sealed class ProblemTypesTests
{
    // A wire contract: the server sends it and clients match on it, so the value must never change silently.
    [Fact]
    public void IdempotencyKeyReused_IsTheAgreedUrn() =>
        ProblemTypes.IdempotencyKeyReused.ShouldBe("urn:nachos:problem:idempotency-key-reused");
}