namespace Nachos.Infra.Tests;

/// <summary>Plants files in a throw-away tree and runs <see cref="UnattendedAzureScanner"/> over it.</summary>
internal static class ScannerFixture
{
    /// <summary>A push-triggered workflow (never exempt) whose single job runs <paramref name="steps"/>.</summary>
    public static string OnPush(string steps) => "on: push\njobs:\n  j:\n    runs-on: ubuntu-latest\n    steps:\n" + steps;

    public static IReadOnlyList<ScanHit> PlantedWorkflowHits(string workflow) => PlantedFileHits(".github/workflows/x.yml", workflow);

    public static IReadOnlyList<ScanHit> PlantedFileHits(string relativePath, string content)
    {
        var root = Directory.CreateTempSubdirectory("nachos-scan-").FullName;
        try
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return new UnattendedAzureScanner(root).Scan();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
