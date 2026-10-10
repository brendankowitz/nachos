using System.Text;
using System.Text.Json;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed class MessageEndpointsTests : ApiTest
{
    [Fact]
    public async Task Create_Batch_Returns201InOrder()
    {
        await SeedSession();
        var messages = await Post(M, """
            {"messages":[{"content":"hello world","peer_id":"p","created_at":"2001-02-03T04:05:06Z"},
            {"content":"second","peer_id":"q","created_at":"1999-01-01T00:00:00Z"}]}
            """, 201);
        messages.GetArrayLength().ShouldBe(2);
        messages[0].GetProperty("content").GetString().ShouldBe("hello world");
        messages[0].GetProperty("token_count").GetInt32().ShouldBe(2);
        messages[0].GetProperty("created_at").GetDateTimeOffset().ShouldBe(new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero));
        messages[1].GetProperty("peer_id").GetString().ShouldBe("q");
        var listed = await Post(M + "/list");
        Ids(listed).ShouldBe(messages.EnumerateArray().Select(x => x.GetProperty("id").GetString()!));
        Ids(await Send(HttpMethod.Get, S + "/peers")).ShouldBe(["p", "q"]);
    }

    [Fact]
    public async Task Create_101_Returns422()
    {
        await SeedSession();
        var body = """{"messages":[""" + string.Join(',', Enumerable.Repeat("""{"content":"a","peer_id":"p"}""", 101)) + "]}";
        Location(await Post(M, body, 422), "body", "messages");
        Ids(await Post(M + "/list")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_ContentOver25000_Returns422()
    {
        await SeedSession();
        var body = $$"""{"messages":[{"content":"{{new string('a', 25001)}}","peer_id":"p"}]}""";
        Location(await Post(M, body, 422), "body", "messages", 0, "content");
    }

    [Fact]
    public async Task Create_TrailingSlashAlias()
    {
        await SeedSession();
        (await Post(M + "/", """{"messages":[{"content":"a","peer_id":"p"}]}""", 201)).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Get_Unknown_Returns404Detail()
    {
        await SeedSession();
        var error = await Send(HttpMethod.Get, M + "/missing", status: 404);
        Problem(error, 404, JsonValueKind.String);
        error.GetProperty("detail").GetString().ShouldBe("Message not found.");
    }

    [Fact]
    public async Task Update_MetadataOnly()
    {
        await SeedSession();
        var created = await Post(M, """{"messages":[{"content":"old","peer_id":"p"}]}""", 201);
        var path = M + "/" + created[0].GetProperty("id").GetString();
        var changed = await Send(HttpMethod.Put, path, """{"content":"ignored","metadata":{"tag":"new"}}""");
        changed.GetProperty("content").GetString().ShouldBe("old");
        changed.GetProperty("metadata").GetProperty("tag").GetString().ShouldBe("new");
        (await Send(HttpMethod.Get, path)).GetRawText().ShouldBe(changed.GetRawText());
    }

    [Fact]
    public async Task Replay_PreservesOriginalEnvelopeAndExactCapturedBody()
    {
        await SeedSession();
        const string body = """{"messages":[{"content":"original","peer_id":"p"}],"unknown":1e2}""";
        var first = await WithKey(M, body, "replay", 201);
        using var document = JsonDocument.Parse(first);
        await Send(HttpMethod.Put, M + "/" + document.RootElement[0].GetProperty("id").GetString(),
            """{"metadata":{"later":true}}""");
        (await WithKey(M + "/", body, "replay", 201)).ShouldBe(first);
        using var conflict = JsonDocument.Parse(await WithKey(M, body.Replace("1e2", "100", StringComparison.Ordinal), "replay", 422));
        conflict.RootElement.GetProperty("type").GetString().ShouldBe("urn:nachos:problem:idempotency-key-reused");
        Ids(await Post(M + "/list")).Length.ShouldBe(1);
    }

    [Theory]
    [InlineData("""{"messages":[{"content":"a","peer_id":"p"}]}""", """{"messages":[{"content":"a","peer_id":"p","configuration":null}]}""")]
    [InlineData("""{"messages":[{"content":"a","peer_id":"p"}],"x":null}""", """{"messages":[{"content":"a","peer_id":"p"}]}""")]
    [InlineData("""{"messages":[{"content":"a","peer_id":"p"}],"x":1}""", """{"messages":[{"content":"a","peer_id":"p"}],"x":2}""")]
    public async Task Replay_UnknownNullOmissionAndConfigurationRemainDistinct(string first, string different)
    {
        await SeedSession();
        await WithKey(M, first, "identity", 201);
        await WithKey(M, different, "identity", 422);
    }

    [Fact]
    public async Task RawDuplicateEnvelope_IsNotCollapsedBeforeCore()
    {
        await SeedSession();
        const string original = """{"messages":[{"content":"a","peer_id":"p"}],"x":1,"x":2}""";
        var first = await WithKey(M, original, "duplicates", 201);
        (await WithKey(M, original, "duplicates", 201)).ShouldBe(first);
        await WithKey(M, """{"messages":[{"content":"a","peer_id":"p"}],"x":2}""", "duplicates", 422);
        Ids(await Post(M + "/list")).Length.ShouldBe(1);
    }

    [Fact]
    [Trait("Upstream", "I1")]
    public async Task TokenCount_UsesOrdinaryTextForSpecialSpellings()
    {
        await SeedSession();
        // Independent ordinary-o200k oracle from the approved tokenizer adjudication: ten adjacent spellings = 61.
        var text = string.Concat(Enumerable.Repeat("<|endoftext|>", 10));
        var result = await Post(M, $$"""{"messages":[{"content":"{{text}}","peer_id":"p"}]}""", 201);
        result[0].GetProperty("token_count").GetInt32().ShouldBe(61);
    }

    [Fact]
    public async Task Metadata65Containers_IsRejectedWithoutMutation()
    {
        await SeedSession();
        var metadata = string.Concat(Enumerable.Repeat("""{"n":""", 65)) + "0" + new string('}', 65);
        await Post(M, """{"messages":[{"content":"a","peer_id":"p","metadata":""" + metadata + "}]}", 422);
        Ids(await Post(M + "/list")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Metadata64Containers_IsAcceptedThroughEnvelopeAndResponse()
    {
        await SeedSession();
        var metadata = string.Concat(Enumerable.Repeat("""{"n":""", 64)) + "0" + new string('}', 64);
        await Post(M, """{"messages":[{"content":"a","peer_id":"p","metadata":""" + metadata + "}]}", 201);
        var list = await Post(M + "/list");
        list.GetProperty("total").GetInt64().ShouldBe(1);
    }

    [Theory]
    [Trait("Upstream", "Corefix3")]
    [InlineData("""{"messages":[{"content":3,"peer_id":"p"}]}""", "content")]
    [InlineData("""{"messages":[{"content":"a"}]}""", "peer_id")]
    public async Task RawSchema_FullMessageLocation(string body, string field)
    {
        await SeedSession();
        Location(await Post(M, body, 422), "body", "messages", 0, field);
    }

    [Theory]
    [Trait("Upstream", "Corefix3")]
    [InlineData("""{"messages":[{"content":"a","peer_id":"p"}],"unknown":"\uD800"}""")]
    [InlineData("""{"messages":[{"content":"a","peer_id":"p","metadata":{"v":"\uD800"}}]}""")]
    public async Task RawUnicode_WithKey_IsCallerErrorNotServerFailure(string body)
    {
        await SeedSession();
        await WithKey(M, body, "unicode", 422);
        Ids(await Post(M + "/list")).ShouldBeEmpty();
    }

    private async Task<string> WithKey(string path, string body, string key, int status)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Add("Idempotency-Key", key);
        using var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).ShouldBe(status, text);
        return text;
    }
}
