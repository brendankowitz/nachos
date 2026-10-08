using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Nachos.Abstractions.Tests.Json;

internal interface IName
{
    string Name { get; }
}

/// <summary>
/// What a caller might hand over as an <see cref="IName"/>: the contract is one property, but the implementation also
/// carries a delegate and a getter that throws. Neither may run while a value holding it is rejected.
/// </summary>
internal sealed class NameProjection : IName
{
    public int ExcludedGetterCalls { get; private set; }

    public int ToStringCalls { get; private set; }

    public string Name => "n";

    public override string ToString()
    {
        ToStringCalls++;
        return "projection";
    }

    [JsonIgnore]
    public Action Callback { get; } = static () => { };

    [JsonIgnore]
    public string Excluded
    {
        get
        {
            ExcludedGetterCalls++;
            throw new InvalidOperationException("excluded getter ran");
        }
    }
}

/// <summary>A class whose extension-data key bypasses the key converter of a serializer.</summary>
internal sealed class WithExtensionData
{
    [JsonExtensionData]
    public Dictionary<string, object> Extra { get; } = new() { ["\uD800"] = 1 };
}

internal sealed class CountingUppercaseConverter : JsonConverter<string>
{
    public int Calls { get; private set; }

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        Calls++;
        writer.WriteStringValue(value.ToUpperInvariant());
    }
}

/// <summary>Sample inputs shared by the helper and the filter parser tests.</summary>
internal static class StrictJsonSamples
{
    private static readonly byte[] Bytes = [1];
    private static readonly int[] Ints = [1];

    /// <summary>Each allowed literal kind with the canonical JSON it must become.</summary>
    public static TheoryData<string, string> AllowedScalars() => new()
    {
        { "bool", "true" },
        { "sbyte", "-5" },
        { "byte", "250" },
        { "short", "-300" },
        { "ushort", "60000" },
        { "int", "-70000" },
        { "uint", "4000000000" },
        { "long", "-9000000000" },
        { "ulong", "18000000000000000000" },
        { "float", "1.5" },
        { "double", "0.1" },
        { "decimal", "12.5" },
        { "char", "\"x\"" },
        { "string", "\"text\"" },
        { "DateTime", "\"2026-01-02T03:04:05Z\"" },
        { "DateTimeOffset", "\"2026-01-02T03:04:05+02:00\"" },
        { "Guid", "\"0f8fad5b-d9cb-469f-a165-70867728950e\"" },
        { "JsonElement", "\"e\"" },
    };

    public static JsonValue Scalar(string kind) => (kind switch
    {
        "bool" => JsonValue.Create(true),
        "sbyte" => JsonValue.Create((sbyte)-5),
        "byte" => JsonValue.Create((byte)250),
        "short" => JsonValue.Create((short)-300),
        "ushort" => JsonValue.Create((ushort)60000),
        "int" => JsonValue.Create(-70000),
        "uint" => JsonValue.Create(4000000000u),
        "long" => JsonValue.Create(-9000000000L),
        "ulong" => JsonValue.Create(18000000000000000000UL),
        "float" => JsonValue.Create(1.5f),
        "double" => JsonValue.Create(0.1),
        "decimal" => JsonValue.Create(12.5m),
        "char" => JsonValue.Create('x'),
        "string" => JsonValue.Create("text"),
        "DateTime" => JsonValue.Create(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)),
        "DateTimeOffset" => JsonValue.Create(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2))),
        "Guid" => JsonValue.Create(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e")),
        "JsonElement" => JsonValue.Create(JsonDocument.Parse("\"e\"").RootElement.Clone()),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    })!;

    /// <summary>Each kind of backing type outside the allowlist.</summary>
    public static TheoryData<string> DisallowedKinds() =>
    [
        "int[]", "List<int>", "Dictionary<string,int>", "POCO", "enum", "Half", "Int128", "UInt128", "TimeSpan",
        "DateOnly", "TimeOnly", "byte[]", "List<JsonNode>", "JsonNode[]",
    ];

    public static (JsonValue Value, Type BackingType) Disallowed(string kind) => kind switch
    {
        "int[]" => (JsonValue.Create(Ints)!, typeof(int[])),
        "List<int>" => (JsonValue.Create(new List<int> { 1 })!, typeof(List<int>)),
        "Dictionary<string,int>" => (JsonValue.Create(new Dictionary<string, int> { ["a"] = 1 })!, typeof(Dictionary<string, int>)),
        "POCO" => (JsonValue.Create(new NameProjection())!, typeof(NameProjection)),
        "enum" => (JsonValue.Create(DayOfWeek.Monday)!, typeof(DayOfWeek)),
        "Half" => (JsonValue.Create((Half)1.5f)!, typeof(Half)),
        "Int128" => (JsonValue.Create((Int128)5)!, typeof(Int128)),
        "UInt128" => (JsonValue.Create((UInt128)5)!, typeof(UInt128)),
        "TimeSpan" => (JsonValue.Create(TimeSpan.FromSeconds(1))!, typeof(TimeSpan)),
        "DateOnly" => (JsonValue.Create(new DateOnly(2026, 1, 2))!, typeof(DateOnly)),
        "TimeOnly" => (JsonValue.Create(new TimeOnly(3, 4))!, typeof(TimeOnly)),
        "byte[]" => (JsonValue.Create(Bytes)!, typeof(byte[])),
        "List<JsonNode>" => (JsonValue.Create(new List<JsonNode> { "ok", "\uD800" })!, typeof(List<JsonNode>)),
        "JsonNode[]" => (JsonValue.Create(new JsonNode[] { new JsonObject() })!, typeof(JsonNode[])),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>An <see cref="IName"/> projection created against the interface's own type info.</summary>
    public static JsonValue InterfaceProjection(NameProjection projection)
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        return JsonValue.Create<IName>(projection, (JsonTypeInfo<IName>)options.GetTypeInfo(typeof(IName)))!;
    }

    /// <summary>A string whose <see cref="JsonTypeInfo"/> would upper-case it through <paramref name="converter"/>.</summary>
    public static JsonValue UppercasedString(string text, CountingUppercaseConverter converter)
    {
        var options = new JsonSerializerOptions { Converters = { converter }, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        return JsonValue.Create(text, (JsonTypeInfo<string>)options.GetTypeInfo(typeof(string)))!;
    }
}
