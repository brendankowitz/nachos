using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions.Json;
using Shouldly;

namespace Nachos.Abstractions.Tests.Json;

public sealed class StrictJsonDataTests
{
    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    private static NachosValidationException Rejected(JsonNode? value, int maxDepth = StrictJsonData.DefaultMaxDepth) =>
        Should.Throw<NachosValidationException>(() => StrictJsonData.ToCanonical(value, maxDepth));

    private static void ShouldRejectBecauseOfType(JsonNode? value, Type backingType)
    {
        var ex = Rejected(value);
        ex.Message.ShouldContain(backingType.ToString());
        ex.Message.ShouldContain("JsonObject");
        ex.Message.ShouldContain("JsonArray");
        ex.Message.ShouldContain("JsonSerializer.SerializeToNode");
    }

    // ------------------------------------------------------------------ acceptance

    [Fact]
    public void Null_ReturnsNull() => StrictJsonData.ToCanonical(null).ShouldBeNull();

    [Theory]
    [MemberData(nameof(StrictJsonSamples.AllowedScalars), MemberType = typeof(StrictJsonSamples))]
    public void AllowedScalar_BecomesItsCanonicalJson(string kind, string expectedJson)
    {
        var canonical = StrictJsonData.ToCanonical(StrictJsonSamples.Scalar(kind)).ShouldNotBeNull();

        JsonNode.DeepEquals(canonical, Parse(expectedJson)).ShouldBeTrue(canonical.ToJsonString());
    }

    [Fact]
    public void ObjectsAndArrays_AreWalkedRecursively_AndKeepTheirOrder()
    {
        var input = new JsonObject
        {
            ["b"] = new JsonArray(1, "two", null, new JsonObject { ["c"] = true }),
            ["a"] = JsonValue.Create(Guid.Empty),
        };

        var canonical = StrictJsonData.ToCanonical(input).ShouldNotBeNull();

        canonical.ToJsonString().ShouldBe("""{"b":[1,"two",null,{"c":true}],"a":"00000000-0000-0000-0000-000000000000"}""");
    }

    [Fact]
    public void ValidSurrogatePairAndLiteralReplacementChar_AcceptedUnchanged()
    {
        var canonical = StrictJsonData.ToCanonical(new JsonArray("\U0001F600", "\uFFFD", Parse("\"\\uD83D\\uDE00\"")));

        canonical!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["\U0001F600", "\uFFFD", "\U0001F600"]);
    }

    [Fact]
    public void JsonBackedNumber_KeepsItsDigits()
    {
        const string digits = "12345678901234567890123.456789012345678901234567890";

        var canonical = StrictJsonData.ToCanonical(Parse($$"""{"n":{{digits}},"t":true,"f":false,"z":null}"""));

        canonical!.ToJsonString().ShouldBe($$"""{"n":{{digits}},"t":true,"f":false,"z":null}""");
    }

    // ------------------------------------------------------------------ nothing of the caller survives

    [Fact]
    public void CustomConverterOnAScalar_NeverRuns_AndTheLiteralValueIsKept()
    {
        var converter = new CountingUppercaseConverter();
        var input = new JsonObject { ["k"] = StrictJsonSamples.UppercasedString("abc", converter) };

        var canonical = StrictJsonData.ToCanonical(input).ShouldNotBeNull();
        var text = canonical.ToJsonString();

        text.ShouldBe("""{"k":"abc"}""");
        converter.Calls.ShouldBe(0);
    }

    [Fact]
    public void InterfaceProjection_Rejected_NamingTheRuntimeType_WithoutRunningCallerCode()
    {
        var projection = new NameProjection();

        ShouldRejectBecauseOfType(StrictJsonSamples.InterfaceProjection(projection), typeof(NameProjection));

        projection.ExcludedGetterCalls.ShouldBe(0);
        projection.ToStringCalls.ShouldBe(0);
    }

    [Fact]
    public void ExtensionDataClass_WithLoneSurrogateKey_Rejected_AsATypeNotAsASurrogate()
    {
        var ex = Rejected(JsonValue.Create(new WithExtensionData()));

        ex.Message.ShouldContain(typeof(WithExtensionData).ToString());
        ex.Message.ShouldNotContain("surrogate");
    }

    [Theory]
    [MemberData(nameof(StrictJsonSamples.DisallowedKinds), MemberType = typeof(StrictJsonSamples))]
    public void DisallowedBackingType_Rejected_NamingTheType(string kind)
    {
        var (value, type) = StrictJsonSamples.Disallowed(kind);

        ShouldRejectBecauseOfType(value, type);
        ShouldRejectBecauseOfType(new JsonObject { ["a"] = new JsonArray(value) }, type);
    }

    [Fact]
    public void Result_IsDetachedFromTheInput()
    {
        var inner = new JsonObject { ["x"] = 1 };
        var array = new JsonArray("a", inner);
        var input = new JsonObject { ["arr"] = array, ["s"] = "before" };

        var canonical = StrictJsonData.ToCanonical(input)!.AsObject();
        var before = canonical.ToJsonString();

        input["s"] = "after";
        input["added"] = 1;
        inner["x"] = 2;
        inner["y"] = 3;
        array.Add("late");
        array[0] = "changed";

        canonical.ToJsonString().ShouldBe(before);
        canonical.ShouldNotBeSameAs(input);
        canonical["arr"].ShouldNotBeSameAs(array);
        canonical["arr"]![1].ShouldNotBeSameAs(inner);
    }

    [Fact]
    public void Result_OfJsonBackedInput_IsDetachedFromItsDocument()
    {
        var document = JsonDocument.Parse("""{"n":1,"s":"text"}""");
        var canonical = StrictJsonData.ToCanonical(JsonValue.Create(document.RootElement.GetProperty("n")))!;

        document.Dispose();

        canonical.ToJsonString().ShouldBe("1");
    }

    // ------------------------------------------------------------------ rejection

    [Fact]
    public void NonFiniteNumbers_Rejected()
    {
        foreach (var number in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Rejected(JsonValue.Create(number));
            Rejected(new JsonObject { ["k"] = new JsonArray(JsonValue.Create(number)) });
        }

        foreach (var number in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Rejected(JsonValue.Create(number));
        }
    }

    [Fact]
    public void LoneSurrogate_InStringCharAndKey_Rejected()
    {
        Rejected(JsonValue.Create<string>("a\uD800"));
        Rejected(JsonValue.Create("\uDC00x"));
        Rejected(JsonValue.Create('\uD800'));
        Rejected(JsonValue.Create('\uDC00'));
        Rejected(new JsonObject { ["k\uD800"] = 1 });
        Rejected(new JsonObject { ["a"] = new JsonObject { ["\uDC00"] = new JsonArray("ok", "bad\uD800") } });
        Rejected(new JsonArray("\uDE00\uD83D"));
    }

    [Fact]
    public void EscapedLoneSurrogate_InJsonBackedStringAndKey_Rejected()
    {
        Rejected(Parse("\"\\uD800\""));
        Rejected(Parse("""{"k":"\uD800"}"""));
        Rejected(Parse("""{"\uDC00":1}"""));
        Rejected(Parse("""{"a":[{"b":"x\uDC00"}]}"""));

        using var document = JsonDocument.Parse("\"\\uD800\"");
        Rejected(JsonValue.Create(document.RootElement));
    }

    [Fact]
    public void DuplicateKeys_InJsonBackedObject_Rejected()
    {
        Rejected(Parse("""{"a":1,"a":2}"""));
        Rejected(Parse("""{"a":{"b":1,"b":2}}"""));
        Rejected(Parse("""{"a":[{"b":1,"b":2}]}"""));
    }

    // ------------------------------------------------------------------ depth

    [Theory]
    [InlineData(63, false)]
    [InlineData(64, false)]
    [InlineData(65, false)]
    [InlineData(63, true)]
    [InlineData(64, true)]
    [InlineData(65, true)]
    public void Depth_CountsContainersOnly_64Accepted_65Rejected(int containers, bool nullLeaf)
    {
        var node = Nest(containers, nullLeaf);

        if (containers <= 64)
        {
            StrictJsonData.ToCanonical(node).ShouldNotBeNull().ToJsonString().ShouldBe(node.ToJsonString());
        }
        else
        {
            Rejected(node).Message.ShouldContain("64");
        }
    }

    [Fact]
    public void Depth_JsonBackedTree_SameLimit()
    {
        var atLimit = string.Concat(Enumerable.Repeat("[", 64)) + string.Concat(Enumerable.Repeat("]", 64));
        StrictJsonData.ToCanonical(JsonNode.Parse(atLimit, documentOptions: new JsonDocumentOptions { MaxDepth = 100 })).ShouldNotBeNull();

        var tooDeep = string.Concat(Enumerable.Repeat("[", 65)) + string.Concat(Enumerable.Repeat("]", 65));
        Rejected(JsonNode.Parse(tooDeep, documentOptions: new JsonDocumentOptions { MaxDepth = 100 }));
    }

    [Fact]
    public void Depth_CustomLimit_IsHonoured()
    {
        StrictJsonData.ToCanonical(Nest(3, nullLeaf: false), maxDepth: 3).ShouldNotBeNull();
        Rejected(Nest(4, nullLeaf: false), maxDepth: 3);
        StrictJsonData.ToCanonical(JsonValue.Create(1), maxDepth: 1).ShouldNotBeNull();
    }

    [Fact]
    public void Depth_LimitBelowOne_IsAProgrammingError() =>
        Should.Throw<ArgumentOutOfRangeException>(() => StrictJsonData.ToCanonical(new JsonObject(), maxDepth: 0));

    // A chain of nested containers: the root plus (containers - 1) objects, ending in a scalar or null.
    private static JsonNode Nest(int containers, bool nullLeaf)
    {
        JsonNode? node = nullLeaf ? null : JsonValue.Create("ok");
        for (var i = 0; i < containers; i++)
        {
            node = i % 2 == 0 ? new JsonObject { ["k"] = node } : new JsonArray(node);
        }

        return node!;
    }
}
