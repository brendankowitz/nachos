using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// The dacpac is the only source of DDL (Task 6). EF Core maps entities and pages queries, and must never create or
/// migrate a schema.
/// </summary>
public sealed class NoEfMigrationsTests
{
    private static readonly string[] SchemaWritingCalls =
    [
        "Database.Migrate",
        "MigrateAsync",
        "EnsureCreated",
        "EnsureDeleted",
        "GenerateCreateScript",
        "HasMigrationsHistoryTable",
    ];

    [Fact]
    public void NoEfMigrations_Exist()
    {
        var project = ProjectDirectory();

        Directory.EnumerateDirectories(project, "Migrations", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .ShouldBeEmpty("the SQL Server provider must not contain an EF Core Migrations folder");

        var sources = Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories).Where(path => !IsBuildOutput(path)).ToList();
        sources.ShouldNotBeEmpty();
        foreach (var source in sources)
        {
            var text = File.ReadAllText(source);
            foreach (var call in SchemaWritingCalls)
            {
                text.ShouldNotContain(call, Case.Sensitive, $"{Path.GetFileName(source)} references {call}");
            }
        }

        var assembly = typeof(SqlMemoryStore).Assembly;
        assembly.GetTypes()
            .Where(type => typeof(Migration).IsAssignableFrom(type) || typeof(ModelSnapshot).IsAssignableFrom(type))
            .ShouldBeEmpty("the assembly must not contain EF Core migrations or a model snapshot");
    }

    private static bool IsBuildOutput(string path) =>
        path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj");

    private static string ProjectDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Nachos.slnx")))
            {
                return Path.Combine(directory.FullName, "src", "DataLayer", "Nachos.DataLayer.SqlServer");
            }
        }

        throw new InvalidOperationException("Nachos.slnx was not found above the test output directory.");
    }
}
