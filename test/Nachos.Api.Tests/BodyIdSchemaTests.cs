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
            (".", "string_pattern_mismatch"),
            ("..", "string_pattern_mismatch"),
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

    public static TheoryData<string, string, string> SessionScopeIdErrors()
    {
        var ids = new (string Field, string Code)[]
        {
            ("", "missing"),
            ("\"id\":null,", "string_type"),
            ("\"id\":7,", "string_type"),
            ("\"id\":true,", "string_type"),
            ("\"id\":[],", "string_type"),
            ("\"id\":{},", "string_type"),
            ("\"id\":\"a b\",", "string_pattern_mismatch"),
            ("\"id\":\".\",", "string_pattern_mismatch"),
            ("\"id\":\"..\",", "string_pattern_mismatch"),
            ("\"id\":\"\",", "string_too_short"),
            ("\"id\":" + JsonSerializer.Serialize(new string('a', 513)) + ",", "string_too_long"),
            ("\"id\":" + JsonSerializer.Serialize(string.Concat(Enumerable.Repeat("\U0001f600", 300))) + ",",
                "string_pattern_mismatch"),
            ("\"id\":" + JsonSerializer.Serialize(string.Concat(Enumerable.Repeat("\U0001f600", 513))) + ",",
                "string_too_long"),
            ("\"id\":\"a\\n\",", "string_pattern_mismatch"),
            ("\"id\":\"\\uD800\",", "value_error"),
        };
        var data = new TheoryData<string, string, string>();
        foreach (var (field, code) in ids)
        {
            foreach (var scopes in new[] { "[\"x\"]", "false", JsonSerializer.Serialize(Enumerable.Repeat("x", 101)) })
            {
                data.Add(field, scopes, code);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(SessionScopeIdErrors))]
    public async Task SessionScopes_IdValidationPrecedesScopeValidationAndDeferral(
        string idField, string scopes, string code)
    {
        await SeedSession();
        var before = await SessionScopeState();
        var error = await Post(W + "/sessions", "{" + idField + "\"scopes\":" + scopes +
            ""","metadata":{"state":"not-stored"},"peers":{"not-stored":{}}}""", 422);
        Location(error, "body", "id");
        error.GetProperty("detail").GetArrayLength().ShouldBe(1);
        error.GetProperty("detail")[0].GetProperty("type").GetString().ShouldBe(code);
        error.GetRawText().ShouldNotContain("not-stored");
        (await SessionScopeState()).ShouldBe(before);
    }

    public static TheoryData<string, int> SessionDeferredIds()
    {
        var data = new TheoryData<string, int>();
        foreach (var id in new[] { "A", new string('a', 512), "s" })
        {
            data.Add(id, 1);
            data.Add(id, 100);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(SessionDeferredIds))]
    public async Task SessionScopes_ValidIdRemainsDeferredBeforeSessionOrPeerMutation(string id, int scopeCount)
    {
        await SeedSession();
        var before = await SessionScopeState();
        var error = await Post(W + "/sessions", JsonSerializer.Serialize(new
        {
            id,
            scopes = Enumerable.Repeat("x", scopeCount),
            metadata = new { state = "not-stored" },
            peers = new Dictionary<string, object> { ["not-stored"] = new { } },
        }), 501);
        Problem(error, 501, JsonValueKind.String);
        (await SessionScopeState()).ShouldBe(before);
    }

    public static TheoryData<string, string, int?> SessionInvalidScopes() => new()
    {
        { "false", "value_error", null },
        { "\"x\"", "value_error", null },
        { "{}", "value_error", null },
        { "[null]", "string_type", 0 },
        { "[\"x\",7]", "value_error", 1 },
        { JsonSerializer.Serialize(Enumerable.Repeat("x", 101)), "too_long", null },
    };

    [Theory]
    [MemberData(nameof(SessionInvalidScopes))]
    public async Task SessionScopes_ValidIdRetainsScopeTypeAndLengthErrors(string scopes, string code, int? index)
    {
        await SeedSession();
        var before = await SessionScopeState();
        var error = await Post(W + "/sessions", "{\"id\":\"new-session\",\"scopes\":" + scopes +
            ""","peers":{"not-stored":{}}}""", 422);
        Location(error, index is { } item ? ["body", "scopes", item] : ["body", "scopes"]);
        error.GetProperty("detail")[0].GetProperty("type").GetString().ShouldBe(code);
        (await SessionScopeState()).ShouldBe(before);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"scopes\":null,")]
    [InlineData("\"scopes\":[],")]
    public async Task SessionScopes_OmittedNullAndEmptyStillCreateSessionAndPeers(string scopesField)
    {
        await SeedSession();
        var created = await Post(W + "/sessions", "{" + scopesField +
            """
            "id":"new-session","metadata":{"state":"created"},"peers":{"created-peer":{}}}
            """);
        created.GetProperty("id").GetString().ShouldBe("new-session");
        created.GetProperty("metadata").GetProperty("state").GetString().ShouldBe("created");
        Ids(await Post(W + "/sessions/list")).ShouldContain("new-session");
        Ids(await Post(W + "/peers/list")).ShouldBe(["created-peer"]);
        Ids(await Send(HttpMethod.Get, W + "/sessions/new-session/peers")).ShouldBe(["created-peer"]);
        Ids(await Send(HttpMethod.Get, S + "/peers")).ShouldBeEmpty();
    }

    private async Task<string[]> SessionScopeState() =>
    [
        (await Post("/v3/workspaces/list")).GetRawText(),
        (await Post(W + "/sessions/list")).GetRawText(),
        (await Post(W + "/peers/list")).GetRawText(),
        (await Send(HttpMethod.Get, S + "/peers")).GetRawText(),
    ];
}
