namespace Nachos.Infra.Tests;

internal static class RepoPaths
{
    /// <summary>The repository root: the nearest ancestor of the test binaries holding <c>Nachos.slnx</c>.</summary>
    public static string Root { get; } = FindRoot();

    public static string Combine(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Nachos.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Nachos.slnx not found above " + AppContext.BaseDirectory);
    }
}
