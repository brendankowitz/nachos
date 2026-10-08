using Microsoft.Extensions.Options;
using Nachos.Core.Keys;
using Shouldly;

namespace Nachos.Cli.Tests;

public sealed class KeyCommandTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef-test-secret";

    private static HmacKeyIssuer Issuer(string kid = "0", string secret = Secret) =>
        new(Options.Create(new SigningKeyOptions { Keys = [new SigningKey(kid, secret)] }), TimeProvider.System);

    private static Task<CliRun> CreateAsync(params string[] args) =>
        CliRun.RunAsync(["keys", "create", .. args]);

    private static string Token(CliRun run)
    {
        run.ExitCode.ShouldBe(0, run.Error);
        run.Out.ShouldEndWith("\n");
        run.Out.Count(c => c == '\n').ShouldBe(1);
        run.Out.ShouldNotContain("\r");
        return run.Out[..^1];
    }

    [Fact]
    public async Task Admin_ValidatesWithIssuer()
    {
        var run = await CreateAsync("--admin", "--signing-secret", Secret);

        var claims = Issuer().Validate(Token(run));
        claims.ShouldBe(new NachosKeyClaims(Admin: true, Workspace: null, Peer: null, Session: null, ExpiresAt: null));
        run.Error.ShouldBeEmpty();
    }

    [Fact]
    public async Task Workspace_Peer_And_Session_ScopesAreCarried()
    {
        var workspace = await CreateAsync("--workspace", "w1", "--signing-secret", Secret);
        var peer = await CreateAsync("--workspace", "w1", "--peer", "p1", "--signing-secret", Secret);
        var session = await CreateAsync("--workspace", "w1", "--session", "s1", "--signing-secret", Secret);

        Issuer().Validate(Token(workspace)).ShouldBe(new NachosKeyClaims(false, "w1", null, null, null));
        Issuer().Validate(Token(peer)).ShouldBe(new NachosKeyClaims(false, "w1", "p1", null, null));
        Issuer().Validate(Token(session)).ShouldBe(new NachosKeyClaims(false, "w1", null, "s1", null));
    }

    [Fact]
    public async Task SecretEnv_IsRead()
    {
        const string variable = "NACHOS_TEST_KEYS_SECRET_READ";
        Environment.SetEnvironmentVariable(variable, Secret);
        try
        {
            var run = await CreateAsync("--admin", "--kid", "0", "--signing-secret-env", variable);

            Issuer().Validate(Token(run)).Admin.ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task Kid_IsCarried()
    {
        var run = await CreateAsync("--admin", "--kid", "rotated-7", "--signing-secret", Secret);
        var token = Token(run);

        Issuer("rotated-7").Validate(token).Admin.ShouldBeTrue();
        // A ring that does not hold that kid cannot validate it.
        Should.Throw<Nachos.Abstractions.AuthException>(() => Issuer("0").Validate(token));
    }

    [Fact]
    public async Task DefaultKid_IsZero()
    {
        var token = Token(await CreateAsync("--admin", "--signing-secret", Secret));

        Issuer("0").Validate(token).Admin.ShouldBeTrue();
        Should.Throw<Nachos.Abstractions.AuthException>(() => Issuer("1").Validate(token));
    }

    [Fact]
    public async Task Expires_InFuture_IsCarried()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(2);

        var run = await CreateAsync("--admin", "--signing-secret", Secret, "--expires", expires.ToString("O"));

        var claims = Issuer().Validate(Token(run));
        claims.ExpiresAt.ShouldNotBeNull();
        (claims.ExpiresAt.Value - expires).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Expires_InPast_Exit1()
    {
        var run = await CreateAsync("--admin", "--signing-secret", Secret, "--expires", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
        run.Error.ShouldContain("--expires");
    }

    [Theory]
    [InlineData("next tuesday")]
    [InlineData("01/02/2030")]
    [InlineData("2030-13-45T00:00:00Z")]
    [InlineData("")]
    public async Task Expires_NotIso8601_Exit1(string expires)
    {
        var run = await CreateAsync("--admin", "--signing-secret", Secret, "--expires", expires);

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
        run.Error.ShouldContain("--expires");
    }

    [Theory]
    [InlineData("2099-01-02T03:04:05Z")]
    [InlineData("2099-01-02T03:04:05+02:00")]
    [InlineData("2099-01-02T03:04:05.123Z")]
    [InlineData("2099-01-02T03:04:05")]
    [InlineData("2099-01-02")]
    public async Task Expires_Iso8601Forms_AreAccepted(string expires)
    {
        var run = await CreateAsync("--admin", "--signing-secret", Secret, "--expires", expires);

        Issuer().Validate(Token(run)).ExpiresAt.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task EmptyKid_Exit1_WithAClearMessage(string kid)
    {
        var run = await CreateAsync("--admin", "--signing-secret", Secret, "--kid", kid);

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
        run.Error.ShouldContain("--kid");
    }

    [Fact]
    public async Task PeerWithoutWorkspace_Exit1()
    {
        var run = await CreateAsync("--peer", "p1", "--signing-secret", Secret);

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
    }

    [Fact]
    public async Task SessionWithoutWorkspace_Exit1()
    {
        var run = await CreateAsync("--session", "s1", "--signing-secret", Secret);

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
    }

    [Fact]
    public async Task Admin_WithPeerButNoWorkspace_Exit1()
    {
        var run = await CreateAsync("--admin", "--peer", "p1", "--signing-secret", Secret);

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
    }

    [Fact]
    public async Task PeerAndSession_Exit1()
    {
        var run = await CreateAsync("--workspace", "w1", "--peer", "p1", "--session", "s1", "--signing-secret", Secret);

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoScope_Exit1()
    {
        var run = await CreateAsync("--signing-secret", Secret);

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("--workspace", "w1")]
    [InlineData("--peer", "p1")]
    [InlineData("--session", "s1")]
    public async Task Admin_WithOtherScope_Exit1(string flag, string value)
    {
        var args = flag == "--workspace" ? new[] { "--admin", flag, value } : ["--admin", "--workspace", "w1", flag, value];

        var run = await CreateAsync([.. args, "--signing-secret", Secret]);

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
    }

    [Fact]
    public async Task SecretEnvMissing_Exit1()
    {
        var run = await CreateAsync("--admin", "--signing-secret-env", "NACHOS_TEST_KEYS_SECRET_DEFINITELY_UNSET");

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
        // The name is whatever the operator typed, which may be the secret itself ("$NACHOS_SIGNING_SECRET" expanded), so it is never echoed.
        run.Error.ShouldContain("--signing-secret-env");
        run.Error.ShouldNotContain("NACHOS_TEST_KEYS_SECRET_DEFINITELY_UNSET");
    }

    [Fact]
    public async Task SecretEnvEmpty_Exit1()
    {
        const string variable = "NACHOS_TEST_KEYS_SECRET_EMPTY";
        Environment.SetEnvironmentVariable(variable, "");
        try
        {
            var run = await CreateAsync("--admin", "--signing-secret-env", variable);

            run.ExitCode.ShouldBe(1);
            run.Out.ShouldBeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task NoSecretSource_Exit1()
    {
        var run = await CreateAsync("--admin");

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
    }

    [Fact]
    public async Task BothSecretSources_Exit1()
    {
        const string variable = "NACHOS_TEST_KEYS_SECRET_BOTH";
        Environment.SetEnvironmentVariable(variable, Secret);
        try
        {
            var run = await CreateAsync("--admin", "--signing-secret", Secret, "--signing-secret-env", variable);

            run.ExitCode.ShouldBe(1);
            run.Out.ShouldBeEmpty();
            run.Error.ShouldNotContain(Secret);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task SecretNeverPrinted()
    {
        const string shortSecret = "short-secret-never-printed";
        var runs = new[]
        {
            await CreateAsync("--admin", "--signing-secret", Secret),
            await CreateAsync("--admin", "--signing-secret", shortSecret),
            await CreateAsync("--signing-secret", shortSecret),
            await CreateAsync("--workspace", "w1", "--peer", "p1", "--session", "s1", "--signing-secret", shortSecret),
            await CreateAsync("--workspace", "not a valid id!", "--signing-secret", Secret),
            await CreateAsync("--admin", "--signing-secret", Secret, "--expires", "2001-01-01T00:00:00Z"),
            // The parser echoes values it cannot place: a response-file path, an unknown option's value, a stray word.
            await CreateAsync("--admin", "--signing-secret", $"@{Secret}"),
            await CreateAsync("--admin", "--signing-secret", Secret, "--bogus", Secret),
            await CreateAsync("--admin", "--bogus", shortSecret),
            await CreateAsync("--admin", "--signing-secret", "first-half-of-an-unquoted-secret", "second-half-of-an-unquoted-secret"),
            await CreateAsync("--admin", "--signing-secret", Secret, "--expires", Secret),
            await CreateAsync("--admin", "--signing-secret", Secret, Secret),
            // A secret typed where the variable's name belongs, which is what an unquoted expansion mistake produces.
            await CreateAsync("--admin", "--signing-secret-env", Secret),
            await CreateAsync("--admin", $"--signing-secret-env={Secret}"),
            await CreateAsync("--admin", $"--signing-secret-env:{Secret}"),
            await CreateAsync("--admin", "--signing-secret", Secret, "--kid", Secret, "--bogus"),
            // '--opt=value' and '--opt:value' are one argument that the parser splits.
            await CreateAsync($"--admin={Secret}", "--signing-secret", Secret),
            await CreateAsync($"--admin:{Secret}", "--signing-secret", Secret),
            await CreateAsync("--admin", $"--bogus={shortSecret}"),
            await CliRun.RunAsync("schema", "upgrade", "--connection", "Server=x", $"--approve-reviewed={Secret}"),
            await CliRun.RunAsync("schema", "upgrade", "--connection", "Server=x", $"--report-only:{Secret}"),
            await CliRun.RunAsync("schema", "upgrade", $"--connection={Secret}", $"--allow-data-loss={shortSecret}"),
        };

        foreach (var run in runs)
        {
            foreach (var secret in new[] { Secret, shortSecret, "first-half-of-an-unquoted-secret", "second-half-of-an-unquoted-secret" })
            {
                run.Error.ShouldNotContain(secret);
            }
        }

        // A token on stdout is expected for a secret that is valid; only the secret itself must not be there.
        foreach (var run in runs.Where(run => run.ExitCode != 0))
        {
            run.Out.ShouldBeEmpty();
        }

        // The too-short secret is rejected by the issuer, so it fails rather than mints a weak key.
        runs[1].ExitCode.ShouldBe(1);
        runs[1].Out.ShouldBeEmpty();
    }

    [Fact]
    public async Task ParseErrors_AreRedacted_ButStillSayWhatIsWrong()
    {
        var unknown = await CreateAsync("--admin", "--signing-secret", Secret, "--bogus", Secret);
        var stray = await CreateAsync("--admin", "--signing-secret", Secret, "stray-word");

        unknown.ExitCode.ShouldBe(1);
        unknown.Error.ShouldContain("redacted");
        stray.ExitCode.ShouldBe(1);
        stray.Error.ShouldContain("redacted");
        stray.Error.ShouldNotContain("stray-word");
    }

    [Theory]
    [InlineData("--workspace")]
    [InlineData("--peer")]
    [InlineData("--expires")]
    public async Task OptionWithoutAValue_SaysSo_EvenWhenAnotherValueIsShort(string option)
    {
        // "a" is a short user token that occurs inside other words of the message; it must not turn the message into a redaction.
        var run = await CreateAsync("--signing-secret", Secret, "--kid", "a", option);

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
        run.Error.ShouldContain($"Required argument missing for option: '{option}'");
        run.Error.ShouldNotContain("redacted");
    }

    [Fact]
    public async Task InvalidWorkspaceId_Exit1()
    {
        var run = await CreateAsync("--workspace", "not a valid id!", "--signing-secret", Secret);

        run.ExitCode.ShouldBe(1);
        run.Out.ShouldBeEmpty();
    }

    [Fact]
    public async Task RedirectedStdout_IsTheTokenAndOneLineFeed_RealProcess()
    {
        var run = await CliRun.RunProcessAsync("keys", "create", "--admin", "--signing-secret", Secret);

        Issuer().Validate(Token(run)).Admin.ShouldBeTrue();
        run.Error.ShouldBeEmpty();
    }
}
