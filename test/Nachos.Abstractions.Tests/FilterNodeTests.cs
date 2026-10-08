using System.Text.Json.Nodes;
using Nachos.Abstractions.Filtering;
using Nachos.Testing.Json;
using Shouldly;

namespace Nachos.Abstractions.Tests;

/// <summary>
/// <see cref="FilterNode.Field"/> and <see cref="FilterNode.MetadataPath"/> are public, so a caller can build one by
/// hand. Each must hold only strict JSON data, whoever built it.
/// </summary>
public sealed class FilterNodeTests
{
    private static JsonValue WithConverter(string text, CountingUppercaseConverter converter) =>
        StrictJsonSamples.UppercasedString(text, converter);

    private static FilterNode.MetadataPath Path(IReadOnlyList<string> keys, JsonNode? value) =>
        new(keys, FilterOp.Eq, value);

    private static FilterNode.Field Name(JsonNode? value) => new(FilterColumns.Name, FilterOp.Eq, value);

    // ------------------------------------------------------------------ callers' converters never run

    [Fact]
    public void Field_WithAConverterOperand_StoresTheLiteral_AndNeverRunsTheConverter()
    {
        var converter = new CountingUppercaseConverter();

        var field = Name(WithConverter("abc", converter));
        var twin = Name(WithConverter("abc", converter));

        field.Value!.GetValue<string>().ShouldBe("abc");
        field.Equals(twin).ShouldBeTrue();
        field.GetHashCode().ShouldBe(twin.GetHashCode());
        field.ToString().ShouldContain("Value = abc");
        field.Value!.ToJsonString().ShouldBe("\"abc\"");
        converter.Calls.ShouldBe(0);
    }

    [Fact]
    public void MetadataPath_WithAConverterOperand_StoresTheLiteral_AndNeverRunsTheConverter()
    {
        var converter = new CountingUppercaseConverter();

        var path = Path(["a", "b"], WithConverter("abc", converter));
        var twin = Path(["a", "b"], WithConverter("abc", converter));

        path.Value!.GetValue<string>().ShouldBe("abc");
        path.Equals(twin).ShouldBeTrue();
        path.GetHashCode().ShouldBe(twin.GetHashCode());
        path.ToString().ShouldContain("\"abc\"");
        converter.Calls.ShouldBe(0);
    }

    [Fact]
    public void ConverterInsideAnArrayOperand_NeverRuns()
    {
        var converter = new CountingUppercaseConverter();

        var path = new FilterNode.MetadataPath(["tags"], FilterOp.JsonContains, new JsonArray(WithConverter("a", converter), "b"));

        path.Value!.ToJsonString().ShouldBe("""["a","b"]""");
        converter.Calls.ShouldBe(0);
    }

    // ------------------------------------------------------------------ rejection

    [Theory]
    [MemberData(nameof(StrictJsonSamples.DisallowedKinds), MemberType = typeof(StrictJsonSamples))]
    public void AnOperandThatIsNotJsonData_IsRejectedAtConstruction(string kind)
    {
        var (value, type) = StrictJsonSamples.Disallowed(kind);

        Should.Throw<NachosValidationException>(() => Name(value)).Message.ShouldContain(type.ToString());
        Should.Throw<NachosValidationException>(() => Path(["a"], StrictJsonSamples.Disallowed(kind).Value));
    }

    [Fact]
    public void AnOperandWithALoneSurrogate_IsRejectedAtConstruction()
    {
        Should.Throw<NachosValidationException>(() => Name("a\uD800"));
        Should.Throw<NachosValidationException>(() => Path(["a"], new JsonArray("ok", "\uDC00")));
        Should.Throw<NachosValidationException>(() => Path(["a"], new JsonObject { ["k\uD800"] = 1 }));
    }

    [Fact]
    public void ALoneSurrogateInAColumnOrAPathKey_IsRejected_WithAFixedMessage()
    {
        Should.Throw<NachosValidationException>(() => new FilterNode.Field("na\uD800me", FilterOp.Eq, "x"))
            .Message.ShouldNotContain("na");
        Should.Throw<NachosValidationException>(() => Path(["ok", "\uDC00"], "x"));
        Should.Throw<NachosValidationException>(() => Path(["\uD800\uD800"], "x"));
    }

    [Fact]
    public void ValidSurrogatePairsAndReplacementCharacters_AreAccepted()
    {
        Name("\U0001F600\uFFFD").Value!.GetValue<string>().ShouldBe("\U0001F600\uFFFD");
        Path(["\U0001F600", "\uFFFD"], "x").Path.ShouldBe(["\U0001F600", "\uFFFD"]);
    }

    [Fact]
    public void NullParts_AreArgumentErrors()
    {
        Should.Throw<ArgumentNullException>(() => new FilterNode.Field(null!, FilterOp.Eq, "x"));
        Should.Throw<ArgumentNullException>(() => new FilterNode.MetadataPath(null!, FilterOp.Eq, "x"));
        Should.Throw<ArgumentNullException>(() => new FilterNode.MetadataPath(["a", null!], FilterOp.Eq, "x"));
    }

    [Fact]
    public void NullOperand_IsAllowed()
    {
        Name(null).Value.ShouldBeNull();
        Path(["a"], null).Value.ShouldBeNull();
    }

    // ------------------------------------------------------------------ detached from the caller

    [Fact]
    public void ChangingTheCallersOperand_AfterConstruction_HasNoEffect()
    {
        var operand = new JsonArray("a", new JsonObject { ["k"] = 1 });
        var path = new FilterNode.MetadataPath(["tags"], FilterOp.JsonContains, operand);
        var before = path.Value!.ToJsonString();

        operand.Add("late");
        operand[0] = "changed";
        ((JsonObject)operand[1]!)["k"] = 2;

        path.Value.ShouldNotBeSameAs(operand);
        path.Value.ToJsonString().ShouldBe(before);
    }

    [Fact]
    public void ChangingTheCallersPathList_AfterConstruction_HasNoEffect()
    {
        var keys = new List<string> { "a", "b" };
        var path = Path(keys, "x");

        keys[0] = "changed";
        keys.Add("late");

        path.Path.ShouldBe(["a", "b"]);
        path.Path.ShouldNotBeOfType<List<string>>();
        ((ICollection<string>)path.Path).IsReadOnly.ShouldBeTrue();
    }

    // ------------------------------------------------------------------ with expressions

    [Fact]
    public void With_CanonicalizesTheOperand()
    {
        var converter = new CountingUppercaseConverter();
        var field = Name("a");
        var path = Path(["a"], "a");

        var changedField = field with { Value = WithConverter("abc", converter) };
        var changedPath = path with { Value = WithConverter("abc", converter) };

        changedField.Value!.GetValue<string>().ShouldBe("abc");
        changedPath.Value!.GetValue<string>().ShouldBe("abc");
        changedField.Value.ToJsonString().ShouldBe("\"abc\"");
        changedPath.Value.ToJsonString().ShouldBe("\"abc\"");
        converter.Calls.ShouldBe(0);
        field.Value!.GetValue<string>().ShouldBe("a");
    }

    [Fact]
    public void With_ValidatesEveryPart()
    {
        var field = Name("a");
        var path = Path(["a"], "a");

        Should.Throw<NachosValidationException>(() => field with { Value = JsonValue.Create(DayOfWeek.Monday) });
        Should.Throw<NachosValidationException>(() => field with { Column = "\uD800" });
        Should.Throw<NachosValidationException>(() => path with { Value = JsonValue.Create(new List<int> { 1 }) });
        Should.Throw<NachosValidationException>(() => path with { Path = ["\uDC00"] });
        Should.Throw<ArgumentNullException>(() => path with { Path = null! });
    }

    [Fact]
    public void With_SnapshotsTheNewPath()
    {
        var keys = new List<string> { "x" };

        var path = Path(["a"], "v") with { Path = keys };
        keys.Add("y");

        path.Path.ShouldBe(["x"]);
    }

    // ------------------------------------------------------------------ what the parser relies on

    [Fact]
    public void ADateTimeOffsetOperand_StaysADateTimeOffset()
    {
        var instant = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var field = new FilterNode.Field(FilterColumns.CreatedAt, FilterOp.Gte, JsonValue.Create(instant));
        var inList = new FilterNode.Field(FilterColumns.CreatedAt, FilterOp.In, new JsonArray(JsonValue.Create(instant)));

        field.Value!.GetValue<DateTimeOffset>().ShouldBe(instant);
        inList.Value!.AsArray()[0]!.GetValue<DateTimeOffset>().ShouldBe(instant);
    }

    [Fact]
    public void ANumericOperand_KeepsItsType()
    {
        new FilterNode.Field(FilterColumns.TokenCount, FilterOp.Gt, JsonValue.Create(5m)).Value!.GetValue<decimal>().ShouldBe(5m);
        new FilterNode.Field(FilterColumns.IsActive, FilterOp.Eq, JsonValue.Create(true)).Value!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void HandBuiltNodes_EqualParserBuiltOnes_AndKeepTheirEqualitySemantics()
    {
        var parsed = FilterParser.Parse("""{"metadata":{"a":{"b":"x"}},"name":"n"}""", ResourceKind.Workspace)
            .ShouldBeOfType<FilterNode.And>();
        var built = new FilterNode.And(
        [
            new FilterNode.MetadataPath(["a", "b"], FilterOp.Eq, JsonValue.Create("x")),
            new FilterNode.Field(FilterColumns.Name, FilterOp.Eq, JsonValue.Create("n")),
        ]);

        built.ShouldBe(parsed);
        built.GetHashCode().ShouldBe(parsed.GetHashCode());
        built.ShouldNotBe(parsed with { Children = [.. parsed.Children.Reverse()] });
        Path(["a"], "x").ShouldNotBe(Path(["a"], "y"));
        Path(["a"], "x").ShouldNotBe(Path(["b"], "x"));
        Name("x").ShouldNotBe(Name("y"));
        Name("x").ShouldBe(Name(JsonNode.Parse("\"x\"")));
    }

    [Fact]
    public void ParsedNodes_KeepTheirTypedOperands()
    {
        var parsed = FilterParser.Parse(
                """{"created_at":{"gte":"2026-01-02T03:04:05Z"},"is_active":true}""",
                ResourceKind.Session)
            .ShouldBeOfType<FilterNode.And>();

        var fields = parsed.Children.OfType<FilterNode.Field>().ToDictionary(f => f.Column);

        fields[FilterColumns.CreatedAt].Value!.GetValue<DateTimeOffset>().ShouldBe(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        fields[FilterColumns.IsActive].Value!.GetValue<bool>().ShouldBeTrue();
    }
}
