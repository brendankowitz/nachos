using System.Security.Cryptography;
using System.Text;
using Shouldly;

namespace Nachos.Cli.Tests;

/// <summary>The DacFx exception (design spec section 3, rule 7) requires the vendor license text to ship with the CLI.</summary>
public sealed class NoticeTests
{
    // Path in the output, and the repo file it must be a byte-for-byte copy of. The license sits at the same repo-relative path
    // as its source (eng/licenses/...), which is where the reviewed notice contract (eng/license-check/DacFxApproval.cs) and
    // the API's publish output put it.
    private static readonly (string Published, string Source)[] Notices =
    [
        ("THIRD-PARTY-NOTICES.md", "THIRD-PARTY-NOTICES.md"),
        (Path.Combine("eng", "licenses", "Microsoft.SqlServer.DacFx", "170.4.83", "license.txt"),
            Path.Combine("eng", "licenses", "Microsoft.SqlServer.DacFx", "170.4.83", "license.txt")),
        (Path.Combine("eng", "licenses", "Microsoft.Data.SqlClient.SNI.runtime", "6.0.3", "LICENSE.txt"),
            Path.Combine("eng", "licenses", "Microsoft.Data.SqlClient.SNI.runtime", "6.0.3", "LICENSE.txt")),
        (Path.Combine("eng", "licenses", "Microsoft.SqlServer.Types", "170.1000.7", "license.md"),
            Path.Combine("eng", "licenses", "Microsoft.SqlServer.Types", "170.1000.7", "license.md")),
    ];

    // Where the license used to be published, which no consumer of the contract looks at.
    private static readonly string OldLicensePath = Path.Combine("licenses", "Microsoft.SqlServer.DacFx", "170.4.83", "license.txt");

    private static void ShouldContainTheNotices(string root)
    {
        foreach (var (published, source) in Notices)
        {
            var copy = Path.Combine(root, published);
            File.Exists(copy).ShouldBeTrue($"{published} is missing from {root}");
            Sha256(copy).ShouldBe(Sha256(Path.Combine(RepoRoot(), source)), $"{published} differs from {source}");
        }

        File.Exists(Path.Combine(root, OldLicensePath)).ShouldBeFalse($"{root} still has the license at the old path {OldLicensePath}");
    }

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
    public void BuildOutput_ContainsBothNotices_ByteIdenticalToTheRepoSources()
    {
        // The CLI's content items flow to this project's output through the project reference, as they do to the CLI's own.
        ShouldContainTheNotices(AppContext.BaseDirectory);
    }

    [Fact]
    public async Task PublishOutput_ContainsBothNotices_ByteIdenticalToTheRepoSources()
    {
        var output = Path.Combine(Path.GetTempPath(), $"nachos-publish-{Guid.NewGuid():N}");
        try
        {
            // No worker node or compiler server may outlive the publish: either would inherit its output pipes and hold them open.
            var (exitCode, stdout, stderr) = await DotnetProcess.RunAsync(
                [
                    "publish", Path.Combine("src", "Nachos.Cli"), "-c", "Release", "-o", output, "--nologo", "-v", "q",
                    "-nodeReuse:false", "-p:UseSharedCompilation=false",
                ],
                TimeSpan.FromMinutes(5),
                RepoRoot());
            exitCode.ShouldBe(0, $"{Encoding.UTF8.GetString(stdout)}{Encoding.UTF8.GetString(stderr)}");

            ShouldContainTheNotices(output);

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
