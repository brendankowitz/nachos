using System.Text.Json;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed class BodyIdSchemaTests : ApiTest
{
    private static readonly string[] Routes = ["/v3/workspaces", W + "/peers", W + "/sessions"];

    public static TheoryData<string, string, string> InvalidIds()
    {
        var cases = new (string Id, string Code)[]
        {
            ("", "string_too_short"),
            (new string('a', 513), "string_too_long"),
            ("private invalid id", "string_pattern_mismatch"),
            ("a\n", "string_pattern_mismatch"),
            ("\u00e9", "string_pattern_mismatch"),
            (string.Concat(Enumerable.Repeat("\U0001f600", 300)), "string_pattern_mismatch"),
            (string.Concat(Enumerable.Repeat("\U0001f600", 512)), "string_pattern_mismatch"),
            (string.Concat(Enumerable.Repeat("\U0001f600", 513)), "string_too_long"),
            (string.Concat(Enumerable.Repeat("e\u0301", 256)), "string_pattern_mismatch"),
            (string.Concat(Enumerable.Repeat("e\u0301", 257)), "string_too_long"),
            (new string('a', 512) + "\n", "string_too_long"),
            ("\n" + new string('a', 512), "string_too_long"),
        };
        var data = new TheoryData<string, string, string>();
        foreach (var route in Routes)
        {
            foreach (var (id, code) in cases)
            {
                data.Add(route, id, code);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(InvalidIds))]
    public async Task InvalidId_IsLocatedSchemaErrorWithoutInsertion(string route, string id, string code)
    {
        await SeedSession();
        var before = (await Post(route + "/list")).GetRawText();
        var error = await Post(route, JsonSerializer.Serialize(new { id, metadata = new { secret = "not-stored" } }), 422);
        Location(error, "body", "id");
        var detail = error.GetProperty("detail");
        detail.GetArrayLength().ShouldBe(1);
        detail[0].GetProperty("type").GetString().ShouldBe(code);
        detail[0].GetProperty("msg").GetString().ShouldNotBeNullOrWhiteSpace();
        error.GetRawText().ShouldNotContain("private invalid id");
        error.GetRawText().ShouldNotContain("not-stored");
        (await Post(route + "/list")).GetRawText().ShouldBe(before);
    }

    public static TheoryData<string, string, string> DecodeCases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var route in Routes)
        {
            data.Add(route, "{}", "missing");
            foreach (var value in new[] { "null", "7", "true", "[]", "{}" })
            {
                data.Add(route, "{\"id\":" + value + "}", "string_type");
            }
            data.Add(route, """{"id":"\uD800"}""", "value_error");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(DecodeCases))]
    public async Task MissingTypeAndDecodeErrors_KeepTheirClassification(string route, string body, string code)
    {
        await SeedSession();
        var before = (await Post(route + "/list")).GetRawText();
        var error = await Post(route, body, 422);
        Location(error, "body", "id");
        error.GetProperty("detail")[0].GetProperty("type").GetString().ShouldBe(code);
        (await Post(route + "/list")).GetRawText().ShouldBe(before);
    }

    public static TheoryData<string, string> ValidIds()
    {
        var data = new TheoryData<string, string>();
        foreach (var route in Routes)
        {
            data.Add(route, "A");
            data.Add(route, new string('a', 512));
            data.Add(route, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ValidIds))]
    public async Task ValidBoundaryAndAlphabetIds_CreateAndGetWithoutOverwriting(string route, string id)
    {
        await SeedSession();
        var original = await Post(route, JsonSerializer.Serialize(new { id, metadata = new { state = "original" } }));
        original.GetProperty("id").GetString().ShouldBe(id);
        original.GetProperty("metadata").GetProperty("state").GetString().ShouldBe("original");
        var existing = await Post(route, JsonSerializer.Serialize(new { id, metadata = new { state = "replacement" } }));
        existing.GetRawText().ShouldBe(original.GetRawText());
        Ids(await Post(route + "/list")).Count(value => value == id).ShouldBe(1);
    }

    [Theory]
    [InlineData("/v3/workspaces")]
    [InlineData(W + "/peers")]
    [InlineData(W + "/sessions")]
    public async Task EscapedAscii_IsValidatedAfterDecoding(string route)
    {
        await SeedSession();
        var created = await Post(route, """{"id":"\u0041\u007a\u0030\u005f\u002d"}""");
        created.GetProperty("id").GetString().ShouldBe("Az0_-");
    }

    [Theory]
    [InlineData("/v3/workspaces")]
    [InlineData(W + "/peers")]
    [InlineData(W + "/sessions")]
    public async Task InvalidId_PrecedesOtherInvalidFields(string route)
    {
        await SeedSession();
        var error = await Post(route, """{"id":"","metadata":[],"configuration":false}""", 422);
        Location(error, "body", "id");
        error.GetProperty("detail")[0].GetProperty("type").GetString().ShouldBe("string_too_short");
    }

    [Theory]
    [InlineData("/v3/workspaces")]
    [InlineData(W + "/peers")]
    [InlineData(W + "/sessions")]
    public async Task ExistingId_DoesNotBypassRequestValidation(string route)
    {
        await SeedSession();
        var original = await Post(route, """{"id":"existing","metadata":{"state":"original"}}""");
        Location(await Post(route, """{"id":"existing","metadata":[]}""", 422), "body", "metadata");
        (await Post(route, """{"id":"existing"}""")).GetRawText().ShouldBe(original.GetRawText());
    }
}
