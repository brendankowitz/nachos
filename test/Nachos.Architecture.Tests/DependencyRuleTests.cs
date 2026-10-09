using System.Reflection;
using System.Xml.Linq;
using Shouldly;

namespace Nachos.Architecture.Tests;

public sealed class DependencyRuleTests
{
    private static readonly string[] ForbiddenForCore =
    [
        "Microsoft.Data.SqlClient",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
    ];

    [Fact]
    public void Core_DoesNotReference_SqlOrAspNet()
    {
        var references = ReferenceNames(typeof(Nachos.Core.AssemblyMarker));

        references.Where(n => ForbiddenForCore.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
            .ShouldBeEmpty();
    }

    [Fact]
    public void Abstractions_ReferencesOnlyBcl()
    {
        var references = ReferenceNames(typeof(Nachos.Abstractions.AssemblyMarker));

        references.Where(n => !IsAllowedForAbstractions(n)).ShouldBeEmpty();
    }

    [Fact]
    public void Client_DoesNotReference_Core()
    {
        var references = ReferenceNames(typeof(Nachos.Client.AssemblyMarker));

        references.Where(n =>
                n == "Nachos.Core" ||
                n == "Nachos.Hosting" ||
                n.StartsWith("Nachos.DataLayer.", StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    [Fact]
    public void Hosting_DoesNotReference_DataLayer()
    {
        var references = ReferenceNames(typeof(Nachos.Hosting.AssemblyMarker));

        references.Where(n => n.StartsWith("Nachos.DataLayer.", StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    // The rules below look at two things. The compiled references of an assembly show what its code actually uses,
    // but the compiler drops a project reference nothing uses, and an assembly this project does not load (Api, Cli,
    // the data layers) has none to read. The declared project references in each .csproj show the dependency itself,
    // by assembly name, whether or not it is built or used yet.

    [Fact]
    public void Core_DependsOnNo_DataLayer_Api_Hosting_Client_Or_Cli()
    {
        var references = AllReferences(typeof(Nachos.Core.AssemblyMarker), "Nachos.Core");

        references.Where(n => IsAny(n, "Nachos.DataLayer", "Nachos.Api", "Nachos.Hosting", "Nachos.Client", "Nachos.Cli"))
            .ShouldBeEmpty();
    }

    [Fact]
    public void Hosting_DependsOnNo_Api_Client_Or_Cli()
    {
        var references = AllReferences(typeof(Nachos.Hosting.AssemblyMarker), "Nachos.Hosting");

        references.Where(n => IsAny(n, "Nachos.Api", "Nachos.Client", "Nachos.Cli")).ShouldBeEmpty();
    }

    [Fact]
    public void Abstractions_DependsOnNoOther_NachosAssembly()
    {
        var references = AllReferences(typeof(Nachos.Abstractions.AssemblyMarker), "Nachos.Abstractions");

        references.Where(n => IsAny(n, "Nachos")).ShouldBeEmpty();
    }

    [Fact]
    public void Client_DependsOnly_OnAbstractions()
    {
        var references = AllReferences(typeof(Nachos.Client.AssemblyMarker), "Nachos.Client");

        references.Where(n => IsAny(n, "Nachos") && !IsAny(n, "Nachos.Abstractions")).ShouldBeEmpty();
        references.Where(n => IsAny(n, "Nachos.Core", "Nachos.DataLayer", "Nachos.Api")).ShouldBeEmpty();
    }

    [Fact]
    public void DataLayer_DependsOnNo_Api_Client_Or_Cli()
    {
        var projects = ProjectsUnder("src/DataLayer");

        projects.ShouldNotBeEmpty("no data layer project was found, so this rule would pass without checking anything");
        foreach (var project in projects)
        {
            DeclaredNachosReferences(project)
                .Where(n => IsAny(n, "Nachos.Api", "Nachos.Client", "Nachos.Cli"))
                .ShouldBeEmpty($"{project} references a forbidden project");
        }
    }

    private static readonly string[] SourceFolders = ["src", "test", "eng"];

    [Fact]
    public void OnlyAppHostTests_Reference_AppHost()
    {
        var root = RepoRoot();
        var projects = SourceFolders
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.csproj", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        projects.ShouldContain(path => Path.GetFileName(path) == "Nachos.AppHost.Tests.csproj",
            "the project allowed to reference the AppHost was not found, so this rule would pass without checking anything");
        foreach (var project in projects.Where(path => Path.GetFileNameWithoutExtension(path) != "Nachos.AppHost.Tests"))
        {
            XDocument.Load(project).Descendants("ProjectReference")
                .Select(reference => Path.GetFileNameWithoutExtension(((string)reference.Attribute("Include")!).Replace('\\', '/')))
                .ShouldNotContain("Nachos.AppHost", $"{Path.GetFileName(project)} references the AppHost; only Nachos.AppHost.Tests may");
        }
    }
    // True when the assembly is one of the given names or lives under it (Nachos.DataLayer covers Nachos.DataLayer.InMemory).
    private static bool IsAny(string assembly, params string[] names) =>
        names.Any(n => assembly == n || assembly.StartsWith(n + ".", StringComparison.Ordinal));

    private static string[] AllReferences(Type marker, string project) =>
        [.. ReferenceNames(marker).Concat(DeclaredNachosReferences(project)).Distinct()];

    /// <summary>The Nachos projects a <c>.csproj</c> (found by name under <c>src</c>) declares as project references.</summary>
    private static string[] DeclaredNachosReferences(string project)
    {
        var matches = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), project + ".csproj", SearchOption.AllDirectories).ToArray();
        matches.Length.ShouldBe(1, $"expected exactly one {project}.csproj under src");

        return [.. XDocument.Load(matches[0]).Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(((string)reference.Attribute("Include")!).Replace('\\', '/')))
            .Where(name => name.StartsWith("Nachos.", StringComparison.Ordinal))];
    }

    /// <summary>The names of the <c>.csproj</c> projects below a repository-relative folder.</summary>
    private static string[] ProjectsUnder(string folder) =>
        [.. Directory.EnumerateFiles(Path.Combine(RepoRoot(), folder), "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()];

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Nachos.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Nachos.slnx was not found above the test output; the declared-reference rules need the source tree.");
    }

    private static string[] ReferenceNames(Type marker) =>
        marker.Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToArray();

    private static bool IsAllowedForAbstractions(string name) =>
        name.StartsWith("System", StringComparison.Ordinal) ||
        name == "netstandard" ||
        (name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) &&
         name.EndsWith(".Abstractions", StringComparison.Ordinal));
}
