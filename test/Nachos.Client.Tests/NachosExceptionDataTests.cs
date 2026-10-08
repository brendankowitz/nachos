using System.Net;
using Nachos.Abstractions;
using Shouldly;

namespace Nachos.Client.Tests;

/// <summary><see cref="NachosExceptionData.TryGetRetryAfter"/>: the typed reading of the Retry-After data entry.</summary>
public sealed class NachosExceptionDataTests
{
    [Fact]
    public void Entry_IsReturned()
    {
        var ex = new HttpRequestException("x");
        ex.Data[NachosExceptionData.RetryAfter] = TimeSpan.FromSeconds(31);

        NachosExceptionData.TryGetRetryAfter(ex, out var delay).ShouldBeTrue();

        delay.ShouldBe(TimeSpan.FromSeconds(31));
    }

    [Fact]
    public void ZeroDelay_IsStillAnEntry()
    {
        var ex = new NotFoundException("x");
        ex.Data[NachosExceptionData.RetryAfter] = TimeSpan.Zero;

        NachosExceptionData.TryGetRetryAfter(ex, out var delay).ShouldBeTrue();

        delay.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void NoEntry_IsFalse()
    {
        NachosExceptionData.TryGetRetryAfter(new HttpRequestException("x"), out var delay).ShouldBeFalse();

        delay.ShouldBe(default);
    }

    [Theory]
    [InlineData("31")]
    [InlineData(31)]
    [InlineData(null)]
    public void EntryOfAnotherType_IsFalse(object? value)
    {
        var ex = new HttpRequestException("x");
        ex.Data[NachosExceptionData.RetryAfter] = value;

        NachosExceptionData.TryGetRetryAfter(ex, out _).ShouldBeFalse();
    }

    [Fact]
    public void NullException_Throws()
    {
        Should.Throw<ArgumentNullException>(() => NachosExceptionData.TryGetRetryAfter(null!, out _));
    }

    [Fact]
    public async Task MappedStatus_IsReadable()
    {
        var stub = new StubHandler((_, _) =>
        {
            var response = StubHandler.Json(HttpStatusCode.TooManyRequests, """{"detail":"slow down"}""");
            response.Headers.TryAddWithoutValidation("Retry-After", "45");
            return response;
        });
        var client = new NachosHttpClient(new HttpClient(stub), new NachosClientOptions { BaseAddress = new Uri("https://nachos.test/") });

        var ex = await Should.ThrowAsync<HttpRequestException>(() => client.GetMessageAsync("w1", "s1", "m1"));

        NachosExceptionData.TryGetRetryAfter(ex, out var delay).ShouldBeTrue();
        delay.ShouldBe(TimeSpan.FromSeconds(45));
    }
}
