using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using Nachos.Abstractions;
using Nachos.Core.Configuration;
using Nachos.Core.Tokens;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class ConfigurationIngressTests
{
    [Theory]
    [InlineData("workspace", "reasoning")]
    [InlineData("workspace", "future")]
    [InlineData("session", "reasoning")]
    [InlineData("session", "future")]
    [InlineData("message", "reasoning")]
    [InlineData("message", "future")]
    public void OpaqueValues_AreRejectedBeforeAnyGetterRuns(string layer, string field)
    {
        var opaque = new OpaqueReasoning();
        var input = new JsonObject { [field] = JsonValue.Create(opaque) };

        var error = Record.Exception(() => Resolve(input, layer));

        opaque.GetterCalls.ShouldBe(0);
        error.ShouldBeOfType<InvalidOperationException>().InnerException
            .ShouldBeOfType<NachosValidationException>().Detail.ShouldContain(nameof(OpaqueReasoning));
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("session")]
    [InlineData("message")]
    public void OpaqueValues_WithAttachedConverters_AreRejectedWithoutRunningThem(string layer)
    {
        var opaque = new OpaqueReasoning();
        var converter = new ReplacementConverter<OpaqueReasoning>();
        var input = new JsonObject { ["future"] = Customized(opaque, converter) };

        var error = Record.Exception(() => Resolve(input, layer));

        converter.Calls.ShouldBe(0);
        opaque.GetterCalls.ShouldBe(0);
        error.ShouldBeOfType<InvalidOperationException>().InnerException.ShouldBeOfType<NachosValidationException>();
    }

    [Theory]
    [InlineData("array")]
    [InlineData("list")]
    [InlineData("dictionary")]
    [InlineData("object projection")]
    public void OpaqueComposites_CannotHideBehindUnknownFields(string kind)
    {
        var opaque = new OpaqueReasoning();
        JsonValue value = kind switch
        {
            "array" => JsonValue.Create(new[] { opaque })!,
            "list" => JsonValue.Create(new List<OpaqueReasoning> { opaque })!,
            "dictionary" => JsonValue.Create(new Dictionary<string, OpaqueReasoning> { ["x"] = opaque })!,
            _ => JsonValue.Create<object>(opaque)!,
        };

        var error = Record.Exception(() => Resolve(new JsonObject { ["future"] = value }));

        opaque.GetterCalls.ShouldBe(0);
        error.ShouldBeOfType<InvalidOperationException>().InnerException.ShouldBeOfType<NachosValidationException>();
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("session")]
    [InlineData("message")]
    public void AttachedScalarConverter_IsIgnoredAndOriginalLiteralIsResolved(string layer)
    {
        var converter = new ReplacementConverter<string>();
        var literal = Customized("original 😀", converter);
        var reasoning = new JsonObject { ["custom_instructions"] = literal };
        var input = new JsonObject { ["reasoning"] = reasoning };

        var result = Resolve(input, layer);

        converter.Calls.ShouldBe(0);
        result.Reasoning.CustomInstructions.ShouldBe(new ResolvedValue<string?>("original 😀", layer));
        reasoning["custom_instructions"].ShouldBeSameAs(literal);
        literal.Parent.ShouldBeSameAs(reasoning);
        reasoning["custom_instructions"] = "changed later";
        result.Reasoning.CustomInstructions.Value.ShouldBe("original 😀");
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("session")]
    [InlineData("message")]
    public void CustomizedJsonElement_IsDetachedWithoutCallingItsConverter(string layer)
    {
        var document = JsonDocument.Parse("""{"enabled":true,"custom_instructions":"keep"}""");
        var converter = new ReplacementConverter<JsonElement>();
        var input = new JsonObject { ["reasoning"] = Customized(document.RootElement, converter) };
        ResolvedConfiguration result;
        using (document)
        {
            result = Resolve(input, layer);
        }

        converter.Calls.ShouldBe(0);
        result.Reasoning.Enabled.ShouldBe(new ResolvedValue<bool?>(true, layer));
        result.Reasoning.CustomInstructions.Value.ShouldBe("keep");
    }

    [Theory]
    [InlineData("char", "x")]
    [InlineData("string", "literal 😀\uFFFD")]
    [InlineData("date", "2026-01-02T03:04:05Z")]
    [InlineData("offset", "2026-01-02T03:04:05+02:00")]
    [InlineData("guid", "0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData("element", "from JSON")]
    public void AllowedStringLiterals_ResolveWithJsonSemantics(string kind, string expected)
    {
        JsonValue literal = kind switch
        {
            "char" => JsonValue.Create('x')!,
            "string" => JsonValue.Create("literal 😀\uFFFD")!,
            "date" => JsonValue.Create(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc))!,
            "offset" => JsonValue.Create(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)))!,
            "guid" => JsonValue.Create(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"))!,
            _ => JsonNode.Parse("\"from JSON\"")!.AsValue(),
        };
        var input = new JsonObject { ["reasoning"] = new JsonObject { ["custom_instructions"] = literal } };

        Resolve(input).Reasoning.CustomInstructions.Value.ShouldBe(expected);
    }

    [Fact]
    public void AllowedNumericAndContainerValues_AreAcceptedInUnknownFields()
    {
        var values = new JsonArray
        {
            null, true, (sbyte)-1, (byte)2, (short)-3, (ushort)4, -5, 6u, -7L, 8UL,
            1.5f, 2.5d, 3.5m, new JsonObject { ["😀"] = false },
            JsonNode.Parse("1234567890123456789012345678901234567890"),
            JsonValue.Create<IComparable>(42),
        };
        var result = Resolve(new JsonObject
        {
            ["future"] = values,
            ["reasoning"] = new JsonObject { ["enabled"] = false },
        });

        result.Reasoning.Enabled.ShouldBe(new ResolvedValue<bool?>(false, "workspace"));
        values.Count.ShouldBe(16);
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("session")]
    [InlineData("message")]
    public void MalformedData_IsRejectedEvenInUnknownFields(string layer)
    {
        JsonObject[] invalid =
        [
            new() { ["reasoning"] = new JsonObject { ["custom_instructions"] = "\uD800" } },
            new() { ["\uDC00"] = "bad key" },
            new() { ["future"] = new JsonArray(new JsonObject { ["x"] = "\uD800" }) },
            new() { ["future"] = double.NaN },
            new() { ["future"] = double.PositiveInfinity },
            new() { ["future"] = JsonValue.Create(default(JsonElement)) },
            JsonNode.Parse("""{"future":{"x":1,"x":2}}""")!.AsObject(),
            JsonNode.Parse("""{"future":"\uD800"}""")!.AsObject(),
            JsonNode.Parse("""{"future":{"\uD800":1}}""")!.AsObject(),
        ];
        foreach (var input in invalid)
        {
            Should.Throw<InvalidOperationException>(() => Resolve(input, layer))
                .InnerException.ShouldBeOfType<NachosValidationException>();
        }
    }

    [Fact]
    public void MalformedScalarWithConverter_CannotBeRepairedBySerialization()
    {
        var converter = new ReplacementConverter<string>();
        var input = new JsonObject { ["custom_instructions"] = Customized("\uD800", converter) };

        var error = Record.Exception(() => Resolve(input));

        converter.Calls.ShouldBe(0);
        error.ShouldBeOfType<InvalidOperationException>().InnerException.ShouldBeOfType<NachosValidationException>();
    }

    [Theory]
    [InlineData("object", 64)]
    [InlineData("array", 64)]
    [InlineData("element", 64)]
    [InlineData("object", 65)]
    [InlineData("array", 65)]
    [InlineData("element", 65)]
    public void DefaultContainerDepth_IsEnforcedBeforeProjection(string kind, int depth)
    {
        JsonNode nested = JsonValue.Create(1)!;
        for (var i = 1; i < depth; i++)
        {
            nested = kind == "object" ? new JsonObject { ["next"] = nested } : new JsonArray(nested);
        }

        var converter = new ReplacementConverter<JsonElement>();
        if (kind == "element")
        {
            using var document = JsonDocument.Parse(nested.ToJsonString(), new JsonDocumentOptions { MaxDepth = 65 });
            nested = Customized(document.RootElement.Clone(), converter);
        }

        var input = new JsonObject
        {
            ["future"] = nested,
            ["reasoning"] = new JsonObject { ["enabled"] = true },
        };

        if (depth == 64)
        {
            Resolve(input).Reasoning.Enabled.Value.ShouldBe(true);
        }
        else
        {
            Should.Throw<InvalidOperationException>(() => Resolve(input))
                .InnerException.ShouldBeOfType<NachosValidationException>();
        }

        converter.Calls.ShouldBe(0);
    }

    [Fact]
    public void DisposedElement_RemainsAProgrammingError()
    {
        var document = JsonDocument.Parse("123");
        var input = new JsonObject { ["future"] = JsonValue.Create(document.RootElement) };
        document.Dispose();

        Should.Throw<ObjectDisposedException>(() => Resolve(input));
    }

    [Fact]
    public void InstructionBudgets_ValidateDeploymentWithoutRevalidatingStoredResources()
    {
        var options = new NachosOptions
        {
            Reasoning = new(CustomInstructions: "long"),
            Deriver = new() { MaxCustomInstructionsTokens = 3 },
        };
        Should.Throw<OptionsValidationException>(() => new ConfigurationResolver(Options.Create(options), new LengthCounter()));
        options.Reasoning = new();
        var resolver = new ConfigurationResolver(Options.Create(options), new LengthCounter());
        var input = new JsonObject { ["reasoning"] = new JsonObject { ["custom_instructions"] = "long" } };

        resolver.Resolve(input).Reasoning.CustomInstructions.Value.ShouldBe("long");
        resolver.Resolve(null, input).Reasoning.CustomInstructions.Value.ShouldBe("long");
        resolver.Resolve(null, null, input).Reasoning.CustomInstructions.Value.ShouldBe("long");
    }

    private static ResolvedConfiguration Resolve(JsonObject input, string layer = "workspace")
    {
        var resolver = new ConfigurationResolver(Options.Create(new NachosOptions()), new LengthCounter());
        return layer switch
        {
            "workspace" => resolver.Resolve(input),
            "session" => resolver.Resolve(null, input),
            "message" => resolver.Resolve(null, null, input),
            _ => throw new ArgumentOutOfRangeException(nameof(layer)),
        };
    }

    private static JsonValue Customized<T>(T value, JsonConverter<T> converter)
    {
        var options = new JsonSerializerOptions
        {
            Converters = { converter },
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        return JsonValue.Create(value, (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)))!;
    }

    private sealed class LengthCounter : ITokenCounter
    {
        public int Count(string text) => text.Length;
    }

    private sealed class OpaqueReasoning
    {
        [JsonIgnore]
        public int GetterCalls { get; private set; }

        [JsonPropertyName("enabled")]
        public bool Enabled
        {
            get
            {
                GetterCalls++;
                return true;
            }
        }
    }

    private sealed class ReplacementConverter<T> : JsonConverter<T>
    {
        public int Calls { get; private set; }

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            Calls++;
            writer.WriteStringValue("replaced");
        }
    }
}
