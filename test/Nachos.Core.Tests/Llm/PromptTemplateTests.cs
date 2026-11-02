using Shouldly;
using Nachos.Core.Llm;

namespace Nachos.Core.Tests.Llm;

public sealed class PromptTemplateTests
{
    private const string Document = "---\nid: test-prompt\nversion: 3\n---\nHello {{name}}, you are {{role}}. Again {{name}}.\n";

    [Fact]
    public void MissingPlaceholder_Throws()
    {
        var template = PromptTemplate.Parse(Document);

        var error = Should.Throw<ArgumentException>(() => template.Render(new Dictionary<string, string> { ["name"] = "Ada" }));

        error.Message.ShouldContain("role");
        error.Message.ShouldContain("test-prompt");
    }

    [Fact]
    public void Render_SubstitutesEveryOccurrence_WithoutRescanningValues()
    {
        var template = PromptTemplate.Parse(Document);

        var text = template.Render(new Dictionary<string, string> { ["name"] = "{{role}}", ["role"] = "admin" });

        text.ShouldBe("Hello {{role}}, you are admin. Again {{role}}.\n");
    }

    [Fact]
    public void Parse_ReadsIdVersionAndPlaceholders()
    {
        var template = PromptTemplate.Parse(Document);

        template.Id.ShouldBe("test-prompt");
        template.Version.ShouldBe("3");
        template.Placeholders.ShouldBe(["name", "role"]);
    }

    [Theory]
    [InlineData("no front matter")]
    [InlineData("---\nid: only-id\n---\nbody")]
    public void Parse_RejectsMissingFrontMatter(string document) =>
        Should.Throw<FormatException>(() => PromptTemplate.Parse(document));

    [Fact]
    public void LoadEmbedded_ReadsAnEmbeddedMarkdownResource()
    {
        var template = PromptTemplate.LoadEmbedded(typeof(PromptTemplateTests).Assembly, "Nachos.Core.Tests.Llm.sample-prompt.md");

        template.Id.ShouldBe("sample");
        template.Placeholders.ShouldBe(["topic"]);
    }
}
