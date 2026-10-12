using System.Reflection;
using Nachos.Abstractions.Contracts;
using Nachos.Core.Configuration;
using Nachos.DataLayer.SqlServer.Schema;

namespace Nachos.DocsGen.Tests;

internal sealed class TestDirectory : IDisposable
{
    internal string Root { get; } = Path.Combine(RealProducerTests.Root, "test", "Nachos.DocsGen.Tests", "obj", "cases",
        Guid.NewGuid().ToString("N"));

    internal TestDirectory() => Directory.CreateDirectory(Root);

    internal string Write(string relative, byte[] bytes)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    internal string Fixture(string mode)
    {
        var assembly = typeof(ProcessFixture).Assembly.Location;
        var path = Write(mode + ".dll", File.ReadAllBytes(assembly));
        Write(mode + ".runtimeconfig.json", File.ReadAllBytes(Path.ChangeExtension(assembly, ".runtimeconfig.json")));
        Write(mode + ".deps.json", File.ReadAllBytes(Path.ChangeExtension(assembly, ".deps.json")));
        return path;
    }

    internal void PrepareOptions()
    {
        foreach (var (assembly, project) in new (Assembly, string)[] {
            (typeof(NachosOptions).Assembly, "src/Nachos.Core"),
            (typeof(SqlServerOptions).Assembly, "src/DataLayer/Nachos.DataLayer.SqlServer"),
            (typeof(ReasoningConfiguration).Assembly, "src/Nachos.Abstractions") })
        {
            var name = assembly.GetName().Name!;
            Write($"{project}/bin/{RealProducerTests.Configuration}/net10.0/{name}.dll", File.ReadAllBytes(assembly.Location));
            var xml = $"eng/DocsGen/obj/options-xml/{RealProducerTests.Configuration}/{name}.xml";
            Write(xml, File.ReadAllBytes(Path.Combine(RealProducerTests.Root, xml)));
        }
        Write("src/Nachos.Api/Program.cs", File.ReadAllBytes(Path.Combine(RealProducerTests.Root, "src/Nachos.Api/Program.cs")));
        foreach (var path in new[] { "src/Nachos.Api/Providers/ProviderSelection.cs",
            "src/Nachos.Api/Auth/AuthenticationRegistration.cs", "src/Nachos.Hosting/NachosSigningKeyConfigurationExtensions.cs" })
            Write(path, File.ReadAllBytes(Path.Combine(RealProducerTests.Root, path)));
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
