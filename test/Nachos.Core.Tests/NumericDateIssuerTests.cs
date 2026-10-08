using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nachos.Abstractions;
using Nachos.Core.Keys;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class NumericDateIssuerTests
{
    private const string Secret = "synthetic-numericdate-repair-key-never-use-for-production-0000000000";

    [Theory]
    [InlineData("nbf", "2000000000", true)]
    [InlineData("exp", "2000000000", false)]
    [InlineData("nbf", "2000000000.00000005", false)]
    [InlineData("exp", "2000000000.00000005", true)]
    [InlineData("nbf", "2000000000.00000000000000000001", false)]
    [InlineData("exp", "2000000000.00000000000000000001", true)]
    public void ParentProbe_UsesExactLifetimeOrdering(string claim, string number, bool accepted)
    {
        AssertLifetime(claim, number, DateTimeOffset.FromUnixTimeSeconds(2_000_000_000), accepted);
    }

    [Theory]
    [InlineData("1999999999.99999999999999999999", 2_000_000_000, -1)]
    [InlineData("2000000000.00000000000000000000", 2_000_000_000, 0)]
    [InlineData("2.00000000000000000000000000001e9", 2_000_000_000, 1)]
    [InlineData("200000000000000000000000000001e-20", 2_000_000_000, 1)]
    [InlineData("20.0000000000000000000000000000E+8", 2_000_000_000, 0)]
    [InlineData("-2000000000.00000000000000000001", -2_000_000_000, -1)]
    [InlineData("-1999999999.99999999999999999999", -2_000_000_000, 1)]
    [InlineData("-2.00000000000000000000000000001e9", -2_000_000_000, -1)]
    [InlineData("-20E8", -2_000_000_000, 0)]
    [InlineData("0", 0, 0)]
    [InlineData("-0.000E+400", 0, 0)]
    [InlineData("0e999999999999999999999", 0, 0)]
    [InlineData("1e-400", 0, 1)]
    [InlineData("-1e-400", 0, -1)]
    [InlineData("1e-999999999999999999999", 0, 1)]
    [InlineData("-1e-999999999999999999999", 0, -1)]
    [InlineData("0.00000010000000000000000000001", 0, 1)]
    public void SignedAndExponentForms_RetainTheirOrder(string number, long unixSeconds, int order)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

        AssertLifetime("exp", number, now, order > 0);
        AssertLifetime("nbf", number, now, order <= 0);
    }

    [Theory]
    [InlineData("exp", "253402300799.9999999", true)]
    [InlineData("exp", "253402300799.9999999000000000001", false)]
    [InlineData("exp", "253402300799.9999998999999999999", true)]
    [InlineData("exp", "2.534023007999999999000000000001e11", false)]
    [InlineData("nbf", "-62135596800", true)]
    [InlineData("nbf", "-62135596800.000000000000000001", false)]
    [InlineData("nbf", "-62135596799.999999999999999999", true)]
    [InlineData("nbf", "-6.2135596800000000000000000001e10", false)]
    [InlineData("exp", "1e999999999999999999999", false)]
    [InlineData("nbf", "-1e999999999999999999999", false)]
    public void SupportedRange_IsCheckedWithoutRounding(string claim, string number, bool accepted)
    {
        AssertLifetime(claim, number, DateTimeOffset.UnixEpoch, accepted);
    }

    [Theory]
    [InlineData("2000000000.00000000000000000001", 20_000_000_000_000_000L)]
    [InlineData("-2000000000.00000000000000000001", -20_000_000_000_000_000L)]
    [InlineData("2000000000.1234567890123456789", 20_000_000_001_234_567L)]
    [InlineData("-2000000000.1234567890123456789", -20_000_000_001_234_567L)]
    [InlineData("253402300799.9999998999999999999", 2_534_023_007_999_999_998L)]
    [InlineData("-62135596799.999999999999999999", -621_355_967_999_999_999L)]
    [InlineData("1e-400", 0L)]
    [InlineData("-1e-400", 0L)]
    public void Projection_TruncatesOnlyAfterExactValidation(string number, long unixTicks)
    {
        var issuer = Create(DateTimeOffset.MinValue);

        var claims = issuer.Validate(Sign("exp", number));

        claims.ExpiresAt.ShouldBe(DateTimeOffset.UnixEpoch.AddTicks(unixTicks));
    }

    [Fact]
    public void VeryLongExponents_AreNotExpandedIntoPowers()
    {
        var exponent = new string('9', 100_000);
        var timer = Stopwatch.StartNew();

        AssertLifetime("exp", "1e-" + exponent, DateTimeOffset.UnixEpoch, true);
        AssertLifetime("nbf", "1e-" + exponent, DateTimeOffset.UnixEpoch, false);
        AssertLifetime("exp", "1e" + exponent, DateTimeOffset.UnixEpoch, false);

        timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    private static void AssertLifetime(string claim, string number, DateTimeOffset now, bool accepted)
    {
        var issuer = Create(now);
        var token = Sign(claim, number);
        if (accepted)
        {
            issuer.Validate(token).Workspace.ShouldBe("W");
        }
        else
        {
            var error = Should.Throw<AuthException>(() => issuer.Validate(token));
            error.Message.ShouldBe("Invalid Nachos key.");
            error.ToString().ShouldNotContain(token);
            error.ToString().ShouldNotContain(Secret);
            error.InnerException.ShouldBeNull();
        }
    }

    private static HmacKeyIssuer Create(DateTimeOffset now) =>
        new(Options.Create(new SigningKeyOptions { Keys = [new("repair", Secret)] }), new FakeTimeProvider(now));

    private static string Sign(string claim, string number)
    {
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(
            $$"""{"t":"2030-01-02T03:04:05Z","w":"W","{{claim}}":{{number}}}""",
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)) { KeyId = "repair" },
                SecurityAlgorithms.HmacSha256));
    }
}
