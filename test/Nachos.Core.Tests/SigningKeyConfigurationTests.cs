using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Nachos.Abstractions;
using Nachos.Core.Keys;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class SigningKeyConfigurationTests
{
    private const string SectionName = "Nachos:Auth:NachosKey";
    private const string SubmittedScalar = "synthetic-misplaced-secret-not-for-production";
    private static readonly string NewSecret = new('n', 32);
    private static readonly string OldSecret = new('o', 32);
    private static readonly NachosKeyClaims Claims = new(true, null, null, null, null);

    public static TheoryData<string, string> MalformedRings()
    {
        var cases = new TheoryData<string, string>();
        foreach (var source in new[] { "environment", "json" })
        foreach (var shape in new[]
        {
            "mixed", "entry", "list", "scalar-list", "kid-only", "secret-only",
            "typo", "unknown", "nested-kid", "nested-secret", "section-scalar",
        })
            cases.Add(source, shape);
        foreach (var shape in new[] { "empty-object", "null-entry", "null-only", "empty-only" })
            cases.Add("json", shape);
        foreach (var shape in new[] { "entry-hybrid", "ring-hybrid" })
            cases.Add("environment", shape);
        return cases;
    }

    [Theory]
    [MemberData(nameof(MalformedRings))]
    public void MalformedConfiguration_CannotSignDisableOrDiscloseValues(string source, string shape)
    {
        using var input = new ConfigurationInput(source, shape);
        var services = new ServiceCollection();
        Register(services, input.Configuration.GetSection(SectionName));
        using var provider = services.BuildServiceProvider();

        var error = Record.Exception(() => provider.GetRequiredService<IKeyIssuer>().Issue(Claims));

        error.ShouldNotBeNull();
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            current.ToString().ShouldNotContain(SubmittedScalar);
            current.ToString().ShouldNotContain(NewSecret);
            current.ToString().ShouldNotContain(OldSecret);
            current.ToString().ShouldNotContain(SectionName);
            current.ToString().ShouldNotContain(input.Prefix);
            current.ToString().ShouldNotContain("Extra");
            current.ToString().ShouldNotContain("Secrte");
        }
        var validation = error.ShouldBeOfType<OptionsValidationException>();
        validation.OptionsType.ShouldBe(typeof(SigningKeyOptions));
        validation.Failures.ShouldNotBeEmpty();
        validation.InnerException.ShouldBeNull();
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("json")]
    public void SingleWhitespaceScalarRing_RejectsWithOnlyConstantDiagnostic(string source)
    {
        using var input = new ConfigurationInput(source, "whitespace");
        var services = new ServiceCollection();
        Register(services, input.Configuration.GetSection(SectionName));
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<TimeProvider>().ShouldNotBeNull();

        var error = Should.Throw<OptionsValidationException>(() =>
            provider.GetRequiredService<IKeyIssuer>().Issue(Claims));

        error.OptionsType.ShouldBe(typeof(SigningKeyOptions));
        error.Failures.ShouldBe(["Keys must be an ordered key list."]);
        error.Message.ShouldBe("Keys must be an ordered key list.");
        error.InnerException.ShouldBeNull();
    }

    [Theory]
    [InlineData("environment", "valid")]
    [InlineData("environment", "lowercase")]
    [InlineData("environment", "gapped")]
    [InlineData("environment", "named")]
    [InlineData("json", "valid")]
    [InlineData("json", "lowercase")]
    [InlineData("json", "gapped")]
    [InlineData("json", "named")]
    public void ValidConfiguration_PreservesProviderOrderAndKeyRotation(string source, string shape)
    {
        using var input = new ConfigurationInput(source, shape);
        var services = new ServiceCollection();
        Register(services, input.Configuration.GetSection(SectionName));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<SigningKeyOptions>>().Value;
        options.Keys.Select(key => key.Kid).ShouldBe(["new", "old"]);
        var issuer = provider.GetRequiredService<IKeyIssuer>();
        var token = issuer.Issue(Claims);
        new JsonWebToken(token).Kid.ShouldBe("new");
        issuer.Validate(token).ShouldBe(Claims);
        var oldIssuer = new HmacKeyIssuer(
            Options.Create(new SigningKeyOptions { Keys = [new("old", OldSecret)] }), TimeProvider.System);
        issuer.Validate(oldIssuer.Issue(Claims)).ShouldBe(Claims);
    }

    [Theory]
    [InlineData("environment", "empty")]
    [InlineData("json", "empty")]
    [InlineData("json", "absent")]
    [InlineData("json", "null-ring")]
    [InlineData("json", "empty-scalar")]
    [InlineData("json", "empty-object-ring")]
    public void EmptyConfiguration_PreservesDisabledIssuance(string source, string shape)
    {
        using var input = new ConfigurationInput(source, shape);
        var services = new ServiceCollection();
        Register(services, input.Configuration.GetSection(SectionName));
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<SigningKeyOptions>>().Value.Keys.ShouldBeEmpty();
        var issuer = provider.GetRequiredService<IKeyIssuer>();
        Should.Throw<NachosValidationException>(() => issuer.Issue(Claims));
    }

    [Fact]
    public void JsonProvider_EmptyArrayAndEmptyScalarHaveIndistinguishableShapes()
    {
        using var array = new ConfigurationInput("json", "empty");
        using var scalar = new ConfigurationInput("json", "empty-scalar");
        array.Configuration.GetSection(SectionName + ":Keys").Value.ShouldBe("");
        scalar.Configuration.GetSection(SectionName + ":Keys").Value.ShouldBe("");
        array.Configuration.AsEnumerable().ShouldBe(scalar.Configuration.AsEnumerable());
    }

    [Fact]
    public void JsonProvider_NullAndEmptyObjectHaveIndistinguishableShapes()
    {
        using var nil = new ConfigurationInput("json", "null-ring");
        using var obj = new ConfigurationInput("json", "empty-object-ring");
        nil.Configuration.GetSection(SectionName + ":Keys").Value.ShouldBeNull();
        obj.Configuration.GetSection(SectionName + ":Keys").GetChildren().ShouldBeEmpty();
        nil.Configuration.AsEnumerable().ShouldBe(obj.Configuration.AsEnumerable());
    }

    [Theory]
    [InlineData("[]", true)]
    [InlineData("\"\"", true)]
    [InlineData("null", false)]
    [InlineData("{}", false)]
    public void JsonProvider_EmptyOverlayDoesNotClearLowerKeyChildren(string overlay, bool rejects)
    {
        using var input = new ConfigurationInput("json", "valid");
        input.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"Nachos:Auth:NachosKey:Keys\":" + overlay + "}")));
        input.Configuration.GetSection(SectionName + ":Keys").GetChildren().Count().ShouldBe(2);
        var services = new ServiceCollection();
        Register(services, input.Configuration.GetSection(SectionName));
        using var provider = services.BuildServiceProvider();

        if (rejects)
        {
            var error = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IKeyIssuer>());
            error.Failures.ShouldBe(["Keys must be an ordered key list."]);
            error.InnerException.ShouldBeNull();
        }
        else
        {
            provider.GetRequiredService<IOptions<SigningKeyOptions>>().Value.Keys.Select(key => key.Kid)
                .ShouldBe(["new", "old"]);
            new JsonWebToken(provider.GetRequiredService<IKeyIssuer>().Issue(Claims)).Kid.ShouldBe("new");
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("empty-scalar")]
    public void StandaloneEmptyBinding_ReplacesProgrammaticRingAndDisablesIssuance(string shape)
    {
        using var input = new ConfigurationInput("json", shape);
        var services = new ServiceCollection();
        services.Configure<SigningKeyOptions>(options => options.Keys = [new("old", OldSecret)]);
        Register(services, input.Configuration.GetSection(SectionName));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<SigningKeyOptions>>().Value.Keys.ShouldBeEmpty();
        Should.Throw<NachosValidationException>(() => provider.GetRequiredService<IKeyIssuer>().Issue(Claims));
    }

    private static void Register(IServiceCollection services, IConfigurationSection section)
    {
        services.AddNachos(builder => builder.BindSigningKeys(section));
    }

    private sealed class ConfigurationInput : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = [];
        public ConfigurationManager Configuration { get; } = new();
        public string Prefix { get; } = "NACHOS_BINDING_TEST_" + Guid.NewGuid().ToString("N") + "_";

        public ConfigurationInput(string source, string shape)
        {
            if (source == "json")
            {
                var good = JsonSerializer.Serialize(new { Kid = "old", Secret = OldSecret });
                var fresh = JsonSerializer.Serialize(new { Kid = "new", Secret = NewSecret });
                var scalar = JsonSerializer.Serialize(SubmittedScalar);
                var keys = shape switch
                {
                    "mixed" => $"[{scalar},{good}]",
                    "entry" => $"[{scalar}]",
                    "list" => scalar,
                    "whitespace" => "\" \"",
                    "scalar-list" => $"[{scalar},{scalar}]",
                    "empty-object" => $"[{{}},{good}]",
                    "null-entry" => $"[null,{good}]",
                    "null-only" => "[null]",
                    "empty-only" => "[{}]",
                    "kid-only" => "[{\"Kid\":\"new\"}]",
                    "secret-only" => $"[{{\"Secret\":{scalar}}}]",
                    "typo" => $"[{{\"Kid\":\"new\",\"Secrte\":{scalar}}},{good}]",
                    "unknown" => $"[{{\"Kid\":\"new\",\"Secret\":{scalar},\"Extra\":{scalar}}}]",
                    "nested-kid" => $"[{{\"Kid\":{{\"child\":{scalar}}},\"Secret\":{scalar}}}]",
                    "nested-secret" => $"[{{\"Kid\":\"new\",\"Secret\":{{\"child\":{scalar}}}}}]",
                    "valid" => $"[{fresh},{good}]",
                    "lowercase" => $"[{{\"kid\":\"new\",\"secret\":\"{NewSecret}\"}},{{\"kid\":\"old\",\"secret\":\"{OldSecret}\"}}]",
                    "gapped" => $"{{\"10\":{good},\"2\":{fresh}}}",
                    "named" => $"{{\"z\":{good},\"a\":{fresh}}}",
                    "null-ring" => "null",
                    "empty-object-ring" => "{}",
                    "empty-scalar" => "\"\"",
                    _ => "[]",
                };
                var section = shape switch
                {
                    "absent" => "{}",
                    "section-scalar" => scalar,
                    _ => "{\"Keys\":" + keys + "}",
                };
                Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(
                    "{\"Nachos\":{\"Auth\":{\"NachosKey\":" + section + "}}}")));
                return;
            }

            var values = new Dictionary<string, string?>();
            switch (shape)
            {
                case "mixed":
                    values["Keys:0"] = SubmittedScalar;
                    AddKey(values, "1", "old", OldSecret);
                    break;
                case "entry": values["Keys:0"] = SubmittedScalar; break;
                case "list": values["Keys"] = SubmittedScalar; break;
                case "whitespace": values["Keys"] = " "; break;
                case "scalar-list":
                    values["Keys:0"] = SubmittedScalar;
                    values["Keys:1"] = SubmittedScalar;
                    break;
                case "kid-only": values["Keys:0:Kid"] = "new"; break;
                case "secret-only": values["Keys:0:Secret"] = SubmittedScalar; break;
                case "typo":
                    values["Keys:0:Kid"] = "new";
                    values["Keys:0:Secrte"] = SubmittedScalar;
                    AddKey(values, "1", "old", OldSecret);
                    break;
                case "unknown":
                    AddKey(values, "0", "new", NewSecret);
                    values["Keys:0:Extra"] = SubmittedScalar;
                    break;
                case "nested-kid":
                    values["Keys:0:Kid:child"] = SubmittedScalar;
                    values["Keys:0:Secret"] = NewSecret;
                    break;
                case "nested-secret":
                    values["Keys:0:Kid"] = "new";
                    values["Keys:0:Secret:child"] = SubmittedScalar;
                    break;
                case "section-scalar": values[""] = SubmittedScalar; break;
                case "entry-hybrid":
                    AddKey(values, "0", "new", NewSecret);
                    values["Keys:0"] = SubmittedScalar;
                    break;
                case "ring-hybrid":
                    AddKey(values, "0", "new", NewSecret);
                    values["Keys"] = SubmittedScalar;
                    break;
                case "valid":
                case "lowercase":
                case "gapped":
                case "named":
                    AddKey(values, shape == "gapped" ? "2" : shape == "named" ? "a" : "0", "new", NewSecret);
                    AddKey(values, shape == "gapped" ? "10" : shape == "named" ? "z" : "1", "old", OldSecret);
                    break;
            }
            try
            {
                foreach (var pair in values)
                {
                    var path = SectionName + (pair.Key.Length == 0 ? "" : ":" + pair.Key);
                    if (shape == "lowercase") path = path.ToLowerInvariant();
                    var name = Prefix + path.Replace(":", "__", StringComparison.Ordinal);
                    _previous.Add(name, Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process));
                    Environment.SetEnvironmentVariable(name, pair.Value, EnvironmentVariableTarget.Process);
                }
                Configuration.AddEnvironmentVariables(Prefix);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static void AddKey(Dictionary<string, string?> values, string index, string kid, string secret)
        {
            values[$"Keys:{index}:Kid"] = kid;
            values[$"Keys:{index}:Secret"] = secret;
        }

        public void Dispose()
        {
            Configuration.Dispose();
            foreach (var pair in _previous)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value, EnvironmentVariableTarget.Process);
        }
    }
}
