using System.Diagnostics;
using System.Security.Cryptography;
using Shouldly;

namespace Nachos.Cli.Tests;

/// <summary>The DacFx exception (design spec section 3, rule 7) requires the vendor license text to ship with the CLI.</summary>
public sealed class NoticeTests
{
    // Path in the publish output, and the repo file it must be a byte-for-byte copy of.
    private static readonly (string Published, string Source)[] Notices =
    [
        ("THIRD-PARTY-NOTICES.md", "THIRD-PARTY-NOTICES.md"),
        (Path.Combine("licenses", "Microsoft.SqlServer.DacFx", "170.4.83", "license.txt"),
            Path.Combine("eng", "licenses", "Microsoft.SqlServer.DacFx", "170.4.83", "license.txt")),
    ];

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Nachos.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Nachos.slnx was not found above the test output directory.");
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task PublishOutput_ContainsBothNotices_ByteIdenticalToTheRepoSources()
    {
        var output = Path.Combine(Path.GetTempPath(), $"nachos-publish-{Guid.NewGuid():N}");
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = RepoRoot(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "publish", Path.Combine("src", "Nachos.Cli"), "-c", "Release", "-o", output, "--nologo", "-v", "q" })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(timeout.Token);
            process.ExitCode.ShouldBe(0, $"{await stdout}{await stderr}");

            foreach (var (published, source) in Notices)
            {
                var copy = Path.Combine(output, published);
                File.Exists(copy).ShouldBeTrue($"{published} is missing from the publish output");
                Sha256(copy).ShouldBe(Sha256(Path.Combine(RepoRoot(), source)), $"{published} differs from {source}");
            }
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }
}
