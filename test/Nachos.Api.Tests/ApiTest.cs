using System.Text;
using System.Text.Json;
using Nachos.Testing;
using Shouldly;

namespace Nachos.Api.Tests;

public abstract class ApiTest : IDisposable
{
    protected NachosApiFactory Factory { get; } = new();
    private HttpClient? _client;
    protected HttpClient Client => _client ??= Factory.CreateClient();
    protected const string W = "/v3/workspaces/w";
    protected const string S = W + "/sessions/s";
    protected const string M = S + "/messages";

    protected async Task<JsonElement> Send(HttpMethod method, string path, string? body = null, int status = 200)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        using var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).ShouldBe(status, text);
        if (status == 204)
        {
            text.ShouldBeEmpty();
            return default;
        }
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = int.MaxValue });
        return document.RootElement.Clone();
    }

    protected Task<JsonElement> Post(string path, string body = "{}", int status = 200) =>
        Send(HttpMethod.Post, path, body, status);

    protected async Task SeedSession()
    {
        await Post("/v3/workspaces", """{"id":"w"}""");
        await Post(W + "/sessions", """{"id":"s"}""");
    }

    protected static string[] Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray();

    protected static void Problem(JsonElement body, int status, JsonValueKind detail)
    {
        body.GetProperty("detail").ValueKind.ShouldBe(detail);
        body.GetProperty("status").GetInt32().ShouldBe(status);
        body.GetProperty("title").GetString().ShouldNotBeNullOrWhiteSpace();
        Uri.IsWellFormedUriString(body.GetProperty("type").GetString(), UriKind.RelativeOrAbsolute).ShouldBeTrue();
    }

    protected static void Location(JsonElement body, params object[] expected)
    {
        Problem(body, 422, JsonValueKind.Array);
        var actual = body.GetProperty("detail")[0].GetProperty("loc").EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.Number ? (object)value.GetInt32() : value.GetString()!);
        actual.ShouldBe(expected);
    }

    public void Dispose()
    {
        _client?.Dispose();
        Factory.Dispose();
        GC.SuppressFinalize(this);
    }
}
