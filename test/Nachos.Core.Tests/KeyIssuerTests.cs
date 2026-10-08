using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nachos.Abstractions;
using Nachos.Core.Keys;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class KeyIssuerTests
{
    private const string FirstSecret = "first-test-key-only-never-for-production-0000000000000000000000000000";
    private const string SecondSecret = "second-test-key-only-never-for-production-000000000000000000000000000";
    private const string ValidBody = """{"t":"2030-01-02T03:04:05Z","exp":2500000000,"w":"Workspace"}""";
    private static readonly DateTimeOffset Now = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void ScopedRoundTrip_UsesTheExactWireClaimsAndInjectedClock()
    {
        var issuer = new HmacKeyIssuer(
            Options.Create(new SigningKeyOptions { Keys = [new("first", FirstSecret)] }),
            new FakeTimeProvider(Now));
        var claims = new NachosKeyClaims(false, "Workspace", "Peer", null, Now.AddHours(1));

        var token = issuer.Issue(claims);
        var parts = token.Split('.');
        using var header = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[0]));
        using var body = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1]));

        header.RootElement.GetProperty("alg").GetString().ShouldBe("HS256");
        header.RootElement.GetProperty("kid").GetString().ShouldBe("first");
        body.RootElement.GetProperty("t").GetString().ShouldBe("2030-01-02T03:04:05.0000000Z");
        body.RootElement.GetProperty("exp").GetInt64().ShouldBe(Now.AddHours(1).ToUnixTimeSeconds());
        body.RootElement.GetProperty("w").GetString().ShouldBe("Workspace");
        body.RootElement.GetProperty("p").GetString().ShouldBe("Peer");
        body.RootElement.EnumerateObject().Select(property => property.Name).Order()
            .ShouldBe(["exp", "p", "t", "w"]);
        issuer.Validate(token).ShouldBe(claims);
    }

    [Theory]
    [InlineData(true, null, null, null)]
    [InlineData(false, "W", null, null)]
    [InlineData(false, "W", "P", null)]
    [InlineData(false, "W", null, "S")]
    [InlineData(true, "W", "P", null)]
    public void OptionalExpiry_RemainsAbsentForExplicitAdminAndScopedKeys(
        bool admin, string? workspace, string? peer, string? session)
    {
        var clock = new FakeTimeProvider(Now);
        var issuer = Create(clock);
        var claims = new NachosKeyClaims(admin, workspace, peer, session, null);

        var token = issuer.Issue(claims);
        using var body = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.Split('.')[1]));

        body.RootElement.TryGetProperty("exp", out _).ShouldBeFalse();
        body.RootElement.TryGetProperty("iat", out _).ShouldBeFalse();
        body.RootElement.TryGetProperty("nbf", out _).ShouldBeFalse();
        body.RootElement.TryGetProperty("ad", out var flag).ShouldBe(admin);
        if (admin)
        {
            flag.GetBoolean().ShouldBeTrue();
        }

        clock.Advance(TimeSpan.FromDays(3650));
        issuer.Validate(token).ShouldBe(claims);
    }

    [Fact]
    public void Expiry_IsExclusiveWithNoSkewAndUsesOnlyInjectedTime()
    {
        var clock = new FakeTimeProvider(Now);
        var issuer = Create(clock);
        var token = issuer.Issue(new(false, "W", null, null, Now.AddSeconds(1)));

        clock.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond - 1));
        issuer.Validate(token).Workspace.ShouldBe("W");
        clock.Advance(TimeSpan.FromTicks(1));
        Should.Throw<AuthException>(() => issuer.Validate(token));
        clock.Advance(TimeSpan.FromHours(1));
        Should.Throw<AuthException>(() => issuer.Validate(token));
    }

    [Fact]
    public void FractionalNumericDate_DoesNotLoseTheRequestedExpiry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(2_000_000_000));
        var issuer = Create(clock);
        var expiry = clock.GetUtcNow().AddMilliseconds(500);
        var token = issuer.Issue(new(false, "W", null, null, expiry));
        using var body = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.Split('.')[1]));

        body.RootElement.GetProperty("exp").GetDecimal().ShouldBe(2_000_000_000.5m);
        issuer.Validate(token).ExpiresAt.ShouldBe(expiry);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Should.Throw<AuthException>(() => issuer.Validate(token));
    }

    [Fact]
    public void OptionalNotBefore_UsesTheClockAndIsInclusive()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(2_000_000_000));
        var issuer = Create(clock);
        var token = Sign("""{"t":"2030-01-02T03:04:05Z","nbf":2000000001.5,"w":"W"}""");

        Should.Throw<AuthException>(() => issuer.Validate(token));
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        issuer.Validate(token).Workspace.ShouldBe("W");
    }

    [Fact]
    public void ExternalSubTickExpiry_IsValidatedBeforeDateTimeOffsetProjection()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(2_000_000_000));
        var issuer = Create(clock);
        var token = Sign("""{"t":"2030-01-02T03:04:05Z","exp":2000000000.00000005,"w":"W"}""");

        issuer.Validate(token).Workspace.ShouldBe("W");
        clock.Advance(TimeSpan.FromTicks(1));
        Should.Throw<AuthException>(() => issuer.Validate(token));
    }

    [Fact]
    public void ExternalSubTickNotBefore_DoesNotPermitEarlyUse()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(2_000_000_000));
        var issuer = Create(clock);
        var token = Sign("""{"t":"2030-01-02T03:04:05Z","nbf":2000000000.00000005,"w":"W"}""");

        Should.Throw<AuthException>(() => issuer.Validate(token));
        clock.Advance(TimeSpan.FromTicks(1));
        issuer.Validate(token).Workspace.ShouldBe("W");
    }

    [Fact]
    public void FirstKeySigns_AndEveryActiveKeyValidatesDuringRotation()
    {
        var first = new SigningKey("first", FirstSecret);
        var second = new SigningKey("second", SecondSecret);
        var older = new HmacKeyIssuer(Options.Create(new SigningKeyOptions { Keys = [first] }), new FakeTimeProvider(Now));
        var rotated = new HmacKeyIssuer(Options.Create(new SigningKeyOptions { Keys = [second, first] }), new FakeTimeProvider(Now));
        var retired = new HmacKeyIssuer(Options.Create(new SigningKeyOptions { Keys = [second] }), new FakeTimeProvider(Now));
        var claims = new NachosKeyClaims(false, "W", null, null, Now.AddHours(1));
        var oldToken = older.Issue(claims);
        var newToken = rotated.Issue(claims);
        using var header = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(newToken.Split('.')[0]));

        header.RootElement.GetProperty("kid").GetString().ShouldBe("second");
        rotated.Validate(oldToken).ShouldBe(claims);
        rotated.Validate(newToken).ShouldBe(claims);
        retired.Validate(newToken).ShouldBe(claims);
        Should.Throw<AuthException>(() => retired.Validate(oldToken));
    }

    [Fact]
    public void MissingKid_SelectsOnlyTheFirstKey()
    {
        var issuer = Create();

        issuer.Validate(Sign(ValidBody, kid: null)).Workspace.ShouldBe("Workspace");
        Should.Throw<AuthException>(() => issuer.Validate(Sign(ValidBody, SecondSecret, kid: null)));
    }

    [Theory]
    [InlineData("unknown", false)]
    [InlineData("FIRST", false)]
    [InlineData("", false)]
    [InlineData("first", true)]
    public void KidSelection_NeverFallsBackToAnotherActiveKey(string kid, bool useSecondSecret)
    {
        var token = Sign(ValidBody, useSecondSecret ? SecondSecret : FirstSecret, kid);

        Should.Throw<AuthException>(() => Create().Validate(token));
    }

    [Theory]
    [InlineData(SecurityAlgorithms.HmacSha384)]
    [InlineData(SecurityAlgorithms.HmacSha512)]
    [InlineData(SecurityAlgorithms.None)]
    public void OnlySignedHs256_IsAccepted(string algorithm)
    {
        var token = Sign(ValidBody, algorithm: algorithm);

        Should.Throw<AuthException>(() => Create().Validate(token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Tampering_IsRejectedWithoutExposingTheToken(bool replacePayload)
    {
        var token = Sign(ValidBody);
        var parts = token.Split('.');
        if (replacePayload)
        {
            parts[1] = Base64UrlEncoder.Encode(ValidBody.Replace("Workspace", "Other", StringComparison.Ordinal));
        }
        else
        {
            parts[2] = (parts[2][0] == 'A' ? "B" : "A") + parts[2][1..];
        }

        token = string.Join('.', parts);
        var error = Should.Throw<AuthException>(() => Create().Validate(token));
        error.ToString().ShouldNotContain(token);
        error.ToString().ShouldNotContain(FirstSecret);
        error.InnerException.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not.a.jwt")]
    [InlineData("e30.e30.")]
    [InlineData("one.two")]
    [InlineData("one.two.three.four.five")]
    public void MalformedToken_IsAnAuthenticationFailure(string? token)
    {
        Should.Throw<AuthException>(() => Create().Validate(token!));
    }

    [Theory]
    [InlineData("t", null)]
    [InlineData("t", "null")]
    [InlineData("t", "1")]
    [InlineData("t", "\"yesterday\"")]
    [InlineData("t", "\"2030-01-02\"")]
    [InlineData("t", "\"2030-01-02T03:04:05\"")]
    [InlineData("t", "\"2030-01-02T03:04:05+01:00\"")]
    [InlineData("ad", "\"true\"")]
    [InlineData("ad", "1")]
    [InlineData("ad", "null")]
    [InlineData("ad", "[]")]
    [InlineData("w", null)]
    [InlineData("w", "null")]
    [InlineData("w", "17")]
    [InlineData("w", "\"\"")]
    [InlineData("w", "\"a b\"")]
    [InlineData("w", "\"é\"")]
    [InlineData("p", "null")]
    [InlineData("p", "true")]
    [InlineData("s", "[]")]
    [InlineData("s", "\"a b\"")]
    [InlineData("exp", "\"2500000000\"")]
    [InlineData("exp", "null")]
    [InlineData("exp", "true")]
    [InlineData("exp", "1e40")]
    [InlineData("exp", "1e1000")]
    [InlineData("exp", "-1e40")]
    [InlineData("exp", "253402300800")]
    [InlineData("nbf", "\"2500000000\"")]
    [InlineData("nbf", "null")]
    [InlineData("nbf", "2600000000")]
    [InlineData("nbf", "1e1000")]
    [InlineData("nbf", "-62135596801")]
    public void MalformedSignedClaims_AreRejected(string name, string? rawValue)
    {
        var body = JsonNode.Parse(ValidBody)!.AsObject();
        if (rawValue is null)
        {
            body.Remove(name);
        }
        else
        {
            body[name] = JsonNode.Parse(rawValue);
        }

        Should.Throw<AuthException>(() => Create().Validate(Sign(body.ToJsonString())));
    }

    [Theory]
    [InlineData("""{"t":"2030-01-02T03:04:05Z","exp":2500000000,"p":"P"}""")]
    [InlineData("""{"t":"2030-01-02T03:04:05Z","exp":2500000000,"s":"S"}""")]
    [InlineData("""{"t":"2030-01-02T03:04:05Z","exp":2500000000,"w":"W","p":"P","s":"S"}""")]
    [InlineData("""{"t":"2030-01-02T03:04:05Z","exp":2500000000,"ad":false}""")]
    [InlineData("""{"t":"2030-01-02T03:04:05Z","exp":2500000000,"w":"W","w":"Other"}""")]
    public void InvalidOrAmbiguousScopes_AreRejected(string body)
    {
        Should.Throw<AuthException>(() => Create().Validate(Sign(body)));
    }

    [Fact]
    public void UnknownClaimsAndUtcOffset_DoNotInventExtraWireRequirements()
    {
        var token = Sign("""{"t":"2030-01-02T03:04:05+00:00","exp":2500000000,"ad":false,"w":"W","extra":{"future":true}}""");

        Create().Validate(token).ShouldBe(new NachosKeyClaims(false, "W", null, null,
            DateTimeOffset.FromUnixTimeSeconds(2_500_000_000)));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, "P", null)]
    [InlineData(null, null, "S")]
    [InlineData("W", "P", "S")]
    [InlineData("a b", null, null)]
    public void Issue_DoesNotMintInvalidNonAdminClaims(string? workspace, string? peer, string? session)
    {
        Should.Throw<NachosValidationException>(() => Create().Issue(new(false, workspace, peer, session, null)));
    }

    [Fact]
    public void MissingSigningKeys_DoNotPreventConstruction_ButNeverMintOrValidate()
    {
        var issuer = new HmacKeyIssuer(Options.Create(new SigningKeyOptions()), new FakeTimeProvider(Now));

        var error = Should.Throw<NachosValidationException>(() => issuer.Issue(new(true, null, null, null, null)));
        error.Detail.ShouldBe("key issuance requires configured signing keys");
        Should.Throw<AuthException>(() => issuer.Validate(Sign(ValidBody)));
    }

    [Theory]
    [InlineData("null-list")]
    [InlineData("null-key")]
    [InlineData("null-kid")]
    [InlineData("blank-kid")]
    [InlineData("duplicate-kid")]
    [InlineData("null-secret")]
    [InlineData("blank-secret")]
    [InlineData("short-secret")]
    public void InvalidConfiguredKeys_AreConfigurationErrorsWithoutSecretValues(string defect)
    {
        IReadOnlyList<SigningKey> keys = defect switch
        {
            "null-list" => null!,
            "null-key" => [null!],
            "null-kid" => [new(null!, FirstSecret)],
            "blank-kid" => [new(" ", FirstSecret)],
            "duplicate-kid" => [new("first", FirstSecret), new("first", SecondSecret)],
            "null-secret" => [new("first", null!)],
            "blank-secret" => [new("first", new string(' ', 64))],
            _ => [new("first", "synthetic-too-short")]
        };

        var error = Should.Throw<OptionsValidationException>(() =>
            new HmacKeyIssuer(Options.Create(new SigningKeyOptions { Keys = keys }), new FakeTimeProvider(Now)));

        error.OptionsType.ShouldBe(typeof(SigningKeyOptions));
        error.OptionsName.ShouldBe(Options.DefaultName);
        error.ToString().ShouldNotContain(FirstSecret);
        error.ToString().ShouldNotContain(SecondSecret);
        error.ToString().ShouldNotContain("synthetic-too-short");
    }

    [Theory]
    [InlineData("""{"alg":"HS256","kid":null}""")]
    [InlineData("""{"alg":"HS256","kid":1}""")]
    [InlineData("""{"alg":"HS256","kid":[]}""")]
    [InlineData("""{"alg":"HS256","kid":"unknown","kid":"first"}""")]
    [InlineData("""{"alg":"none","alg":"HS256","kid":"first"}""")]
    [InlineData("""{"alg":true,"kid":"first"}""")]
    public void MalformedSignedHeaders_DoNotCoerceOrFallBack(string header)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(FirstSecret));
        var provider = CryptoProviderFactory.Default.CreateForSigning(key, SecurityAlgorithms.HmacSha256);
        string token;
        try
        {
            var input = Base64UrlEncoder.Encode(header) + "." + Base64UrlEncoder.Encode(ValidBody);
            token = input + "." + Base64UrlEncoder.Encode(provider.Sign(Encoding.ASCII.GetBytes(input)));
        }
        finally
        {
            CryptoProviderFactory.Default.ReleaseSignatureProvider(provider);
        }

        Should.Throw<AuthException>(() => Create().Validate(token));
    }

    [Fact]
    public void OversizedToken_IsRejectedAsAuthRatherThanLeakingLibraryErrors()
    {
        Should.Throw<AuthException>(() => Create().Validate(new string('a', 1_048_577)));
    }

    [Theory]
    [InlineData('a', 31, false)]
    [InlineData('a', 32, true)]
    [InlineData('é', 16, true)]
    public void SigningSecretMinimum_Is256Utf8BitsNotCharacters(char value, int count, bool valid)
    {
        var options = Options.Create(new SigningKeyOptions { Keys = [new("first", new string(value, count))] });
        if (!valid)
        {
            Should.Throw<OptionsValidationException>(() => new HmacKeyIssuer(options, new FakeTimeProvider(Now)));
            return;
        }

        var issuer = new HmacKeyIssuer(options, new FakeTimeProvider(Now));
        var claims = new NachosKeyClaims(false, "W", null, null, null);
        issuer.Validate(issuer.Issue(claims)).ShouldBe(claims);
    }

    [Fact]
    public void CapturedKeyRing_IsNotMutatedByLaterOptionsEdits()
    {
        var keys = new List<SigningKey> { new("first", FirstSecret) };
        var issuer = new HmacKeyIssuer(Options.Create(new SigningKeyOptions { Keys = keys }), new FakeTimeProvider(Now));
        keys.Clear();

        issuer.Validate(issuer.Issue(new(false, "W", null, null, null))).Workspace.ShouldBe("W");
    }

    [Fact]
    public async Task SharedIssuer_CanSignAndValidateConcurrentIndependentScopes()
    {
        var issuer = Create();
        var work = Enumerable.Range(0, 64).Select(index => Task.Run(() =>
        {
            var claims = new NachosKeyClaims(false, $"W{index}", null, null, null);
            issuer.Validate(issuer.Issue(claims)).ShouldBe(claims);
        }));

        await Task.WhenAll(work);
    }

    [Fact]
    public void SigningKeyDiagnosticString_DoesNotDiscloseTheSecret()
    {
        var key = new SigningKey("first", FirstSecret);

        key.ToString().ShouldNotContain(FirstSecret);
    }

    [Fact]
    public void UnexpectedClockFailures_AreNotMisreportedAsBadTokens()
    {
        var error = new InvalidOperationException("synthetic clock failure");
        var issuer = new HmacKeyIssuer(
            Options.Create(new SigningKeyOptions { Keys = [new("first", FirstSecret)] }),
            new FailingClock(error));

        Should.Throw<InvalidOperationException>(() => issuer.Validate(Sign(ValidBody))).ShouldBeSameAs(error);
    }

    private sealed class FailingClock(Exception error) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw error;
    }

    private static HmacKeyIssuer Create(FakeTimeProvider? clock = null) =>
        new(Options.Create(new SigningKeyOptions
        {
            Keys = [new("first", FirstSecret), new("second", SecondSecret)]
        }), clock ?? new FakeTimeProvider(Now));

    private static string Sign(string payload, string secret = FirstSecret, string? kid = "first",
        string algorithm = SecurityAlgorithms.HmacSha256)
    {
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return algorithm == SecurityAlgorithms.None
            ? handler.CreateToken(payload)
            : handler.CreateToken(payload,
                new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)) { KeyId = kid },
                    algorithm));
    }
}
