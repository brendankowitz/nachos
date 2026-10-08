using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nachos.Abstractions;
using Nachos.Core.Keys;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class SigningKeyBindingTests
{
    private static readonly NachosKeyClaims Claims = new(true, null, null, null, null);
    private static readonly string FirstSecret = new('n', 32);
    private static readonly string SecondSecret = new('o', 32);

    public static TheoryData<int, int, string> InvalidEntries()
    {
        var cases = new TheoryData<int, int, string>();
        foreach (var (count, index) in new[] { (1, 0), (3, 0), (3, 1), (3, 2) })
        {
            foreach (var defect in new[] { "missing kid", "missing secret", "typo secret", "empty kid",
                "blank kid", "empty secret", "blank secret", "short secret" })
            {
                cases.Add(count, index, defect);
            }
        }
        return cases;
    }

    [Theory]
    [MemberData(nameof(InvalidEntries))]
    public void InvalidBoundEntry_FailsBeforeSigningWithoutFallingBack(int count, int index, string defect)
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i < count; i++)
        {
            values[$"Keys:{i}:Kid"] = $"key-{i}";
            values[$"Keys:{i}:Secret"] = FirstSecret;
        }
        var prefix = $"Keys:{index}:";
        switch (defect)
        {
            case "missing kid": values.Remove(prefix + "Kid"); break;
            case "missing secret": values.Remove(prefix + "Secret"); break;
            case "typo secret":
                values.Remove(prefix + "Secret");
                values[prefix + "Secrte"] = FirstSecret;
                break;
            case "empty kid": values[prefix + "Kid"] = ""; break;
            case "blank kid": values[prefix + "Kid"] = " "; break;
            case "empty secret": values[prefix + "Secret"] = ""; break;
            case "blank secret": values[prefix + "Secret"] = new string(' ', 32); break;
            case "short secret": values[prefix + "Secret"] = new string('x', 31); break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(values);
        var options = new SigningKeyOptions();
        configuration.Bind(options);

        var error = Should.Throw<OptionsValidationException>(() =>
            new HmacKeyIssuer(Options.Create(options), TimeProvider.System));
        error.Failures.ShouldHaveSingleItem().ShouldContain($"Keys[{index}]");
        options.Keys.Count.ShouldBe(count);
        options.ToString()!.ShouldNotContain(FirstSecret);
        options.Keys[index].ToString().ShouldNotContain(FirstSecret);

        var services = new ServiceCollection();
        services.Configure<SigningKeyOptions>(configuration.Bind);
        services.AddNachos(_ => { });
        using var provider = services.BuildServiceProvider();
        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IKeyIssuer>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidBoundRing_PreservesOrderExactKidsAndNoKidKeyZero(bool reverse)
    {
        var first = reverse ? "old" : "new";
        var second = reverse ? "new" : "old";
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Keys:0:Kid"] = first, ["Keys:0:Secret"] = FirstSecret,
                ["Keys:1:Kid"] = second, ["Keys:1:Secret"] = SecondSecret,
            });
        var options = new SigningKeyOptions();
        configuration.Bind(options);
        options.Keys.Select(key => key.Kid).ShouldBe([first, second]);
        var issuer = new HmacKeyIssuer(Options.Create(options), TimeProvider.System);

        var issued = issuer.Issue(Claims);
        new JsonWebToken(issued).Kid.ShouldBe(first);
        issuer.Validate(issued).ShouldBe(Claims);
        issuer.Validate(Sign(null, FirstSecret)).ShouldBe(Claims);
        issuer.Validate(Sign(second, SecondSecret)).ShouldBe(Claims);
        Should.Throw<AuthException>(() => issuer.Validate(Sign(null, SecondSecret)));
        Should.Throw<AuthException>(() => issuer.Validate(Sign(first.ToUpperInvariant(), FirstSecret)));
    }

    [Fact]
    public void EmptyBoundRing_RemainsDisabledRatherThanInvalid()
    {
        using var configuration = new ConfigurationManager();
        var options = new SigningKeyOptions();
        configuration.Bind(options);
        var issuer = new HmacKeyIssuer(Options.Create(options), TimeProvider.System);

        options.Keys.ShouldBeEmpty();
        Should.Throw<NachosValidationException>(() => issuer.Issue(Claims));
    }

    [Fact]
    public void DirectNamedConstructionAndWith_PreserveRecordCompatibilityAndRedaction()
    {
        var original = new SigningKey(Kid: "first", Secret: FirstSecret);
        var changed = original with { Kid = "second", Secret = SecondSecret };
        var (kid, secret) = changed;
        kid.ShouldBe("second");
        secret.ShouldBe(SecondSecret);
        original.ShouldBe(new SigningKey("first", FirstSecret));
        changed.ToString().ShouldNotContain(SecondSecret);
        var issuer = new HmacKeyIssuer(Options.Create(new SigningKeyOptions { Keys = [changed] }), TimeProvider.System);
        new JsonWebToken(issuer.Issue(Claims)).Kid.ShouldBe("second");
    }

    private static string Sign(string? kid, string secret)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)) { KeyId = kid };
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = new Dictionary<string, object> { ["ad"] = true, ["t"] = "2030-01-01T00:00:00Z" },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });
    }
}
