using System.Security.Cryptography;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed class NoticeTests
{
    [Fact]
    public void BuildOutput_ContainsMicrosoftNotices_ByteIdenticalToTheRepoSources()
    {
        var root = RepoRoot();
        string[] notices =
        [
            "THIRD-PARTY-NOTICES.md",
            Path.Combine("eng", "licenses", "Microsoft.SqlServer.DacFx", "170.4.83", "license.txt"),
            Path.Combine("eng", "licenses", "Microsoft.Data.SqlClient.SNI.runtime", "6.0.3", "LICENSE.txt"),
            Path.Combine("eng", "licenses", "Microsoft.SqlServer.Types", "170.1000.7", "license.md"),
        ];
        foreach (var relative in notices)
        {
            var copy = Path.Combine(AppContext.BaseDirectory, relative);
            File.Exists(copy).ShouldBeTrue($"{relative} is missing from the API build output.");
            Hash(copy).ShouldBe(Hash(Path.Combine(root, relative)), $"{relative} differs from the repository source.");
        }
        File.Exists(Path.Combine(AppContext.BaseDirectory, "licenses", "Microsoft.SqlServer.DacFx", "170.4.83", "license.txt"))
            .ShouldBeFalse("The obsolete DacFx notice path must not reappear.");
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Nachos.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("Nachos.slnx was not found above the API test output.");
    }
}
