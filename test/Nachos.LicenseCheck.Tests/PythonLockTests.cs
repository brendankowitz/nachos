using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Nachos.LicenseCheck;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class PythonLockTests
{
    private const string LockPath = "test/conformance/python/requirements.lock";
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('B', 64);
    private static readonly string[] CurrentIdentities =
    [
        "annotated-types==0.8.0", "anyio==4.15.1", "certifi==2026.7.22", "colorama==0.4.6",
        "h11==0.16.0", "honcho-ai==2.5.1", "httpcore==1.0.9", "httpx==0.28.1", "idna==3.20",
        "iniconfig==2.3.1", "packaging==26.3", "pluggy==1.6.0", "pydantic==2.14.0",
        "pydantic-core==2.50.0", "pygments==2.21.0", "pytest==9.1.1",
        "typing-extensions==4.16.0", "typing-inspection==0.4.4"
    ];

    [Theory]
    [InlineData(" ; sys_platform == 'win32'")]
    [InlineData(";sys_platform==\"win32\"")]
    [InlineData("\t;\tsys_platform\t==\t'win32'")]
    public void ConditionalRecord_IsCollectedWithoutEvaluatingHost(string marker)
    {
        using var fixture = new AuditFixture();
        fixture.Python("colorama", "MIT", AuditFixture.Mit);
        fixture.WriteText(LockPath, $"colorama==1.0.0{marker} --hash=sha256:{WheelHash(fixture, "colorama")}\n");

        var report = fixture.Check();
        report.Errors.ShouldBeEmpty();
        report.Packages.Where(package => package.Ecosystem == "python")
            .Select(package => $"{package.Package}@{package.Version}").ShouldBe(["colorama@1.0.0"]);
    }

    [Fact]
    public void MissingConditionalIdentity_IsNotMaskedByDuplicateArchiveCount()
    {
        using var fixture = new AuditFixture();
        fixture.Python("client", "MIT", AuditFixture.Mit);
        File.Copy(WheelPath(fixture, "client"), fixture.Full("python-archives/duplicate.whl"));
        fixture.WriteText(LockPath, $"client==1.0.0 --hash=sha256:{WheelHash(fixture, "client")}\n"
            + $"colorama==0.4.6 ; sys_platform == 'win32' --hash=sha256:{HashA}\n");

        var report = fixture.Check();
        report.Errors.ShouldContain(error => error.Contains("required Python archive missing for python:colorama@0.4.6", StringComparison.Ordinal));
        report.Packages.ShouldNotContain(package => package.Package == "colorama");
    }

    [Fact]
    public void EveryArchive_MustMatchLockedIdentityAndHash()
    {
        using var fixture = new AuditFixture();
        fixture.Python("client", "MIT", AuditFixture.Mit);
        fixture.WriteText(LockPath, $"client==1.0.0 --hash=sha256:{HashA}\n");
        var report = fixture.Check();
        report.Errors.ShouldContain(error => error.Contains("Python archive SHA256 does not match", StringComparison.Ordinal));
        report.Packages.ShouldNotContain(package => package.Ecosystem == "python");

        fixture.WriteText(LockPath, $"other==1.0.0 --hash=sha256:{WheelHash(fixture, "client")}\n");
        report = fixture.Check();
        report.Errors.ShouldContain(error => error.Contains("not in requirements.lock: client@1.0.0", StringComparison.Ordinal));
        report.Errors.ShouldContain(error => error.Contains("required Python archive missing for python:other@1.0.0", StringComparison.Ordinal));
        report.Packages.ShouldNotContain(package => package.Ecosystem == "python");
    }

    [Fact]
    public void ExtraUnlistedArchive_FailsEvenWithAllRequiredIdentitiesPresent()
    {
        using var fixture = new AuditFixture();
        fixture.Python("client", "MIT", AuditFixture.Mit);
        fixture.Python("extra", "MIT", AuditFixture.Mit);
        fixture.WriteText(LockPath, $"client==1.0.0 --hash=sha256:{WheelHash(fixture, "client")}\n");
        var report = fixture.Check();
        report.Errors.ShouldContain(error => error.Contains("not in requirements.lock: extra@1.0.0", StringComparison.Ordinal));
        report.Packages.ShouldContain(package => package.Package == "client");
        report.Packages.ShouldNotContain(package => package.Package == "extra");
    }

    [Fact]
    public void MatchingArchiveWithoutLockHash_IsRejected()
    {
        using var fixture = new AuditFixture();
        fixture.Python("client", "MIT", AuditFixture.Mit);
        fixture.WriteText(LockPath, "client==1.0.0\n");
        fixture.Check().Errors.ShouldContain(error => error.Contains("SHA256", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Projection_PreservesEveryCurrentLockIdentityAndHashWithoutWritingInput()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Nachos.slnx")))
            directory = directory.Parent;
        directory.ShouldNotBeNull();
        var path = Path.Combine(directory.FullName, LockPath.Replace('/', Path.DirectorySeparatorChar));
        var original = File.ReadAllBytes(path);
        var sourceRecords = ReadFixtureRecords(File.ReadAllText(path));
        sourceRecords.Keys.Order(StringComparer.Ordinal).ShouldBe(CurrentIdentities.Order(StringComparer.Ordinal));
        sourceRecords["pydantic-core==2.50.0"].Length.ShouldBe(137);

        var result = await Command("project-python", "--lock", path);
        result.Exit.ShouldBe(0, result.Error);
        result.Error.ShouldBeEmpty();
        result.Output.ShouldNotContain(";");
        result.Output.ShouldNotContain("#");
        AssertSameRecords(sourceRecords, ReadFixtureRecords(result.Output));
        File.ReadAllBytes(path).ShouldBe(original);

        using var fixture = new AuditFixture();
        fixture.WriteText(LockPath, result.Output);
        var repeated = await Command("project-python", "--lock", fixture.Full(LockPath));
        repeated.Exit.ShouldBe(0, repeated.Error);
        repeated.Output.ShouldBe(result.Output);
        AssertSameRecords(sourceRecords, ReadFixtureRecords(repeated.Output));
    }

    [Fact]
    public async Task Projection_StripsOnlySupportedAnnotationCommentsAndFormatting()
    {
        using var fixture = new AuditFixture();
        var text = $"# audit-only projection\r\nFoo_bar==1.2.3+local ; sys_platform == \"win32\" \\\r\n"
            + $"  --hash=sha256:{HashA} \\\r\n\t--hash=sha256:{HashB} # evidence\r\n";
        fixture.WriteText(LockPath, text);
        var result = await Command("project-python", "--lock", fixture.Full(LockPath));
        result.Exit.ShouldBe(0, result.Error);
        result.Error.ShouldBeEmpty();
        result.Output.ShouldBe($"Foo_bar==1.2.3+local --hash=sha256:{HashA} --hash=sha256:{HashB}\n");
        File.ReadAllText(fixture.Full(LockPath)).ShouldBe(text);
    }

    [Theory]
    [InlineData("client==1.0.0", "SHA256")]
    [InlineData("client==1.0.0 ;", "marker")]
    [InlineData("client==1.0.0 ; python_version == '3.13'", "marker")]
    [InlineData("client==1.0.0 ; sys_platform != 'win32'", "marker")]
    [InlineData("client==1.0.0 ; sys_platform == 'linux'", "marker")]
    [InlineData("client==1.0.0 ; sys_platform == win32", "marker")]
    [InlineData("client==1.0.0 ; sys_platform == 'win32\"", "marker")]
    [InlineData("client==1.0.0 ; sys_platform === 'win32'", "marker")]
    [InlineData("client==1.0.0 ; sys_platform == 'win32';", "marker")]
    [InlineData("client==1.0.0 ; sys_platform == 'win32' or os_name == 'nt'", "marker")]
    [InlineData("client==1.0.0 ; --hash", "marker")]
    [InlineData("client==1.0.0 ; --hash=sha256:bad", "marker")]
    [InlineData("client==1.0.0 ; sys_platform == 'win32' --index-url=https://example.test", "requirement")]
    [InlineData("client==1.0.0 ; sys_platform == 'win32' --hash", "requirement")]
    [InlineData("client==1.0.0 --index-url=https://example.test", "requirement")]
    [InlineData("client>=1.0.0", "requirement")]
    [InlineData("client==1.*", "requirement")]
    [InlineData("client @ https://example.test/client.whl", "requirement")]
    [InlineData("git+https://example.test/client.git", "requirement")]
    [InlineData("-r other.lock", "requirement")]
    [InlineData("--index-url https://example.test", "requirement")]
    public async Task InvalidLock_IsRejectedByBothCollectorAndProjection(string record, string diagnostic)
    {
        using var fixture = new AuditFixture();
        fixture.Python("client", "MIT", AuditFixture.Mit);
        var text = record == "client==1.0.0" ? record : $"{record} --hash=sha256:{HashA}";
        fixture.WriteText(LockPath, text);
        fixture.Check().Errors.ShouldContain(error => error.Contains(diagnostic, StringComparison.OrdinalIgnoreCase));
        var result = await Command("project-python", "--lock", fixture.Full(LockPath));
        result.Exit.ShouldBe(2);
        result.Output.ShouldBeEmpty();
        result.Error.ShouldContain(diagnostic, Case.Insensitive);
        File.ReadAllText(fixture.Full(LockPath)).ShouldBe(text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# comment only\n")]
    [InlineData("client==1.0.0 --hash=sha256:abc")]
    [InlineData("client==1.0.0 --hash=sha512:HASH")]
    [InlineData("client==1.0.0 --hash=sha256:HASH\\")]
    [InlineData("client==1.0.0 --hash=sha256:HASH\\\n")]
    [InlineData("client==1.0.0 --hash=sha256:HASH\nclient==1.0.0 --hash=sha256:HASH")]
    [InlineData("Foo_bar==1.0.0 --hash=sha256:HASH\nfoo-bar==1.0.0 --hash=sha256:HASH")]
    public async Task MalformedOrDuplicateLock_FailsWithoutPartialStdout(string text)
    {
        using var fixture = new AuditFixture();
        fixture.WriteText(LockPath, text.Replace("HASH", HashA, StringComparison.Ordinal));
        var result = await Command("project-python", "--lock", fixture.Full(LockPath));
        result.Exit.ShouldBe(2);
        result.Output.ShouldBeEmpty();
        result.Error.ShouldContain("LICENSE INPUT ERROR:");
    }

    [Fact]
    public async Task Projection_LateInvalidRecordEmitsNothing()
    {
        using var fixture = new AuditFixture();
        fixture.WriteText(LockPath, $"client==1.0.0 --hash=sha256:{HashA}\ninvalid>=2.0\n");
        var result = await Command("project-python", "--lock", fixture.Full(LockPath));
        result.Exit.ShouldBe(2);
        result.Output.ShouldBeEmpty();
        result.Error.ShouldContain("requirement");
    }

    [Theory]
    [InlineData("--output")]
    [InlineData("--report")]
    [InlineData("--repo")]
    [InlineData("--lock")]
    public async Task Projection_RejectsOtherAndDuplicateOptionsWithoutWriting(string option)
    {
        using var fixture = new AuditFixture();
        fixture.WriteText(LockPath, $"client==1.0.0 --hash=sha256:{HashA}\n");
        var target = fixture.Full("must-not-write.txt");
        var result = await Command("project-python", "--lock", fixture.Full(LockPath), option, target);
        result.Exit.ShouldBe(2);
        result.Output.ShouldBeEmpty();
        result.Error.ShouldContain("argument");
        File.Exists(target).ShouldBeFalse();
    }

    [Fact]
    public async Task Projection_MissingLockAndMissingValueAreInputErrors()
    {
        foreach (var args in new[] { new[] { "project-python" }, new[] { "project-python", "--lock" } })
        {
            var result = await Command(args);
            result.Exit.ShouldBe(2);
            result.Output.ShouldBeEmpty();
            result.Error.ShouldContain("--lock");
        }
    }

    private static Dictionary<string, string[]> ReadFixtureRecords(string text)
    {
        // Independent fixture oracle: blocks start with literal identity lines, not the production parser.
        var blocks = Regex.Split(text, @"(?m)(?=^[A-Za-z0-9][A-Za-z0-9._-]*==)");
        return blocks.Where(block => block.Length != 0 && char.IsAsciiLetterOrDigit(block[0])).ToDictionary(
            block => Regex.Match(block, @"\A[^ \t\r\n;\\]+").Value,
            block => Regex.Matches(block, @"--hash=sha256:([a-fA-F0-9]{64})")
                .Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }

    private static void AssertSameRecords(Dictionary<string, string[]> expected, Dictionary<string, string[]> actual)
    {
        actual.Keys.Order(StringComparer.Ordinal).ShouldBe(expected.Keys.Order(StringComparer.Ordinal));
        foreach (var (identity, hashes) in expected) actual[identity].ShouldBe(hashes, identity);
    }

    private static string WheelPath(AuditFixture fixture, string name) =>
        fixture.Full($"python-archives/{name.Replace('-', '_')}-1.0.0-py3-none-any.whl");

    private static string WheelHash(AuditFixture fixture, string name) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(WheelPath(fixture, name))));

    private static async Task<(int Exit, string Output, string Error)> Command(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(LicenseAudit).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout, await stderr);
    }
}
