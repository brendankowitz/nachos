using System.Globalization;
using Shouldly;

namespace Nachos.DocsGen.Tests;

public sealed class FailureTests
{
    [Theory]
    [InlineData("--openapi", "absent.json")]
    [InlineData("--openapi", "malformed.json")]
    [InlineData("--cli", "absent.dll")]
    [InlineData("--manifest", "malformed.json")]
    [InlineData("--configuration", "secret-value")]
    [InlineData("--unknown-secret", "secret-value")]
    [InlineData("--openapi", "..\\secret-value")]
    public async Task FailureIsNonzeroValueFreeAndPreservesAllOldOutputs(string option, string value)
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Root, "output");
        var old = OldOutputs();
        AtomicOutputs.Publish(destination, old);
        directory.Write("malformed.json", Inputs.Utf8.GetBytes("{secret-value"));
        if (value.EndsWith(".json", StringComparison.Ordinal) || value.EndsWith(".dll", StringComparison.Ordinal))
            value = Path.Combine(directory.Root, value);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        string[] configurationArgs = option == "--configuration" ? [] : ["--configuration", RealProducerTests.Configuration];
        (await Program.RunAsync([.. configurationArgs, "--root", RealProducerTests.Root, "--output", destination, option, value], output, error))
            .ShouldBe(1);
        output.ToString().ShouldBeEmpty();
        error.ToString().ShouldNotContain("secret-value");
        error.ToString().ShouldNotContain(directory.Root);
        foreach (var (name, bytes) in old) File.ReadAllBytes(Path.Combine(destination, name)).ShouldBe(bytes);
        Directory.GetFileSystemEntries(directory.Root).Any(p => p.Contains(".docsgen-", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public void DuplicateArgumentsAndOutputOverlappingSourceAreRejected()
    {
        Should.Throw<InvalidDataException>(() => Inputs.Parse(["--root", RealProducerTests.Root, "--root", "secret"]));
        Should.Throw<InvalidDataException>(() => Inputs.Parse(["--root", RealProducerTests.Root, "--output", "src"]));
        Should.Throw<InvalidDataException>(() => Inputs.Parse(["--openapi"]));
    }

    [Fact]
    public void MissingXmlFailsRatherThanFabricatingDocumentation()
    {
        using var directory = new TestDirectory();
        directory.PrepareOptions();
        File.Delete(Path.Combine(directory.Root, $"eng/DocsGen/obj/options-xml/{RealProducerTests.Configuration}/Nachos.Core.xml"));
        Should.Throw<InvalidDataException>(() => OptionsReference.Render(
            Inputs.Parse(["--configuration", RealProducerTests.Configuration, "--root", directory.Root])));
    }

    [Fact]
    public void WrongAssemblyXmlFails()
    {
        using var directory = new TestDirectory();
        directory.PrepareOptions();
        directory.Write($"eng/DocsGen/obj/options-xml/{RealProducerTests.Configuration}/Nachos.Core.xml",
            Inputs.Utf8.GetBytes("<doc><assembly><name>Wrong</name></assembly><members/></doc>"));
        Should.Throw<InvalidDataException>(() => OptionsReference.Render(
            Inputs.Parse(["--configuration", RealProducerTests.Configuration, "--root", directory.Root])));
    }

    [Fact]
    public void XmlExternalEntitiesAreNeverResolved()
    {
        using var directory = new TestDirectory();
        directory.PrepareOptions();
        directory.Write($"eng/DocsGen/obj/options-xml/{RealProducerTests.Configuration}/Nachos.Core.xml",
            Inputs.Utf8.GetBytes("""<!DOCTYPE doc [<!ENTITY secret SYSTEM "file:///secret">]><doc>&secret;</doc>"""));
        Should.Throw<System.Xml.XmlException>(() => OptionsReference.Render(
            Inputs.Parse(["--configuration", RealProducerTests.Configuration, "--root", directory.Root])));
    }

    [Fact]
    public void ChangedInputAfterSnapshotFails()
    {
        using var directory = new TestDirectory();
        var file = directory.Write("input.txt", [1]);
        var inputs = Inputs.Parse(["--root", directory.Root]);
        _ = inputs.Read(file);
        File.WriteAllBytes(file, [2]);
        Should.Throw<InvalidDataException>(inputs.VerifyUnchanged);
    }

    [Fact]
    public async Task FailedRealProcessDoesNotReplaceOutputsOrEchoChildStderr()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Root, "output");
        var old = OldOutputs();
        AtomicOutputs.Publish(destination, old);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        (await Program.RunAsync(["--configuration", RealProducerTests.Configuration, "--root", RealProducerTests.Root,
            "--output", destination, "--cli", directory.Fixture("nonzero")],
            TextWriter.Null, error)).ShouldBe(1);
        error.ToString().ShouldNotContain("synthetic-secret-must-not-leak");
        foreach (var (name, bytes) in old) File.ReadAllBytes(Path.Combine(destination, name)).ShouldBe(bytes);
    }

    [Fact]
    public void StaleOutputsAreReplacedAsOneGeneration()
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Root, "output");
        AtomicOutputs.Publish(destination, OldOutputs());
        var fresh = AtomicOutputs.Names.ToDictionary(name => name, name => Inputs.Utf8.GetBytes("new-" + name));
        AtomicOutputs.Publish(destination, fresh);
        foreach (var (name, bytes) in fresh) File.ReadAllBytes(Path.Combine(destination, name)).ShouldBe(bytes);
        Directory.GetFileSystemEntries(directory.Root).Length.ShouldBe(1);
    }

    [Theory]
    [InlineData("unowned")]
    [InlineData("locked")]
    [InlineData("incomplete")]
    public void PublicationFailuresNeverDeleteOrPartiallyReplaceOldFiles(string mode)
    {
        using var directory = new TestDirectory();
        var destination = Path.Combine(directory.Root, "output");
        var old = OldOutputs();
        AtomicOutputs.Publish(destination, old);
        if (mode == "unowned") File.WriteAllText(Path.Combine(destination, "unowned.txt"), "keep");
        if (mode == "locked") File.WriteAllText(destination + ".docsgen-lock", "other generator");
        var fresh = OldOutputs();
        if (mode == "incomplete") fresh.Remove("options.md");
        Should.Throw<Exception>(() => AtomicOutputs.Publish(destination, fresh));
        foreach (var (name, bytes) in old) File.ReadAllBytes(Path.Combine(destination, name)).ShouldBe(bytes);
        if (mode == "unowned") File.ReadAllText(Path.Combine(destination, "unowned.txt")).ShouldBe("keep");
        if (mode == "locked") File.ReadAllText(destination + ".docsgen-lock").ShouldBe("other generator");
    }

    private static Dictionary<string, byte[]> OldOutputs() => AtomicOutputs.Names.ToDictionary(
        name => name, name => Inputs.Utf8.GetBytes("old-" + name), StringComparer.Ordinal);
}
