using System.Reflection;
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
