using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nachos.Abstractions;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed class StrictConfigurationUtf8Tests : ApiTest
{
    private static readonly (string Method, string Path)[] Routes =
    [
        ("POST", "/v3/workspaces"), ("PUT", W), ("POST", W + "/sessions"), ("PUT", S),
    ];

    public static TheoryData<string, string, string, string> InvalidBytes()
    {
        var data = new TheoryData<string, string, string, string>();
        foreach (var (method, path) in Routes)
        {
            foreach (var hex in new[] { "FF", "C0AF", "EDA080" })
            {
                foreach (var configuration in new[]
                {
                    """{"custom_instructions":"@@"}""",
                    """{"@@":1}""",
                    """{"future":"@@"}""",
                    """{"reasoning":{"@@":true}}""",
                })
                {
                    data.Add(method, path, hex, configuration);
                }
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(InvalidBytes))]
    public async Task RawInvalidUtf8_IsLocated422WithoutMutationOrServerFailure(
        string method, string path, string hex, string configuration)
    {
        var errors = new ErrorLog();
        var observed = new ExceptionObserver();
        using var factory = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Insert(0, ServiceDescriptor.Singleton<IExceptionHandler>(observed));
            services.AddLogging(logging => logging.AddProvider(errors));
        }));
        using var client = factory.CreateClient();
        await Seed(client);
        var before = await State(client);
        var body = Payload(Envelope(method, configuration), Convert.FromHexString(hex));
        var (status, text) = await Raw(client, method, path, body);
        status.ShouldBe(422, text + Environment.NewLine + string.Join(Environment.NewLine, errors.Events));
        using var document = JsonDocument.Parse(text);
        Location(document.RootElement, "body", "configuration");
        var detail = document.RootElement.GetProperty("detail");
        detail.GetArrayLength().ShouldBe(1);
        detail[0].GetProperty("type").GetString().ShouldBe("json_invalid");
        detail[0].GetProperty("msg").GetString().ShouldNotBeNullOrWhiteSpace();
        text.ShouldNotContain("InvalidOperationException");
        text.ShouldNotContain("RequestBody");
        text.ShouldNotContain("not-stored");
        errors.Events.ShouldBeEmpty();
        observed.Exceptions.ShouldHaveSingleItem().ShouldBeOfType<RequestValidationException>()
            .InnerException.ShouldBeOfType<InvalidOperationException>();
        (await State(client)).ShouldBe(before);
    }

    public static TheoryData<string, string, string, string> ValidBytes()
    {
        var data = new TheoryData<string, string, string, string>();
        foreach (var (method, path) in Routes)
        {
            data.Add(method, path, "C3A9", "\u00e9");
            data.Add(method, path, "F09F9880", "\U0001f600");
            data.Add(method, path, "EFBFBD", "\ufffd");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ValidBytes))]
    public async Task ValidMultibyteAndReplacementCharacter_AreStoredUnchanged(
        string method, string path, string hex, string expected)
    {
        await Seed(Client);
        var (status, text) = await Raw(Client, method, path,
            Payload(Envelope(method, """{"custom_instructions":"@@"}"""), Convert.FromHexString(hex)));
        status.ShouldBe(200, text);
        using var document = JsonDocument.Parse(text);
        document.RootElement.GetProperty("configuration").GetProperty("custom_instructions").GetString().ShouldBe(expected);
        var itemPath = method == "POST" ? path : path == W ? "/v3/workspaces" : W + "/sessions";
        var id = method == "POST" ? "new" : path == W ? "w" : "s";
        var stored = await Post(itemPath, JsonSerializer.Serialize(new { id }));
        stored.GetProperty("configuration").GetProperty("custom_instructions").GetString().ShouldBe(expected);
    }

    public static TheoryData<string, string, string> DomainCases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (method, path) in Routes)
        {
            foreach (var configuration in new[]
            {
                """{"custom_instructions":"\uD800"}""",
                """{"\uD800":1}""",
                """{"future":"\uD800"}""",
                """{"reasoning":{"\uD800":true}}""",
                """{"future":1,"future":2}""",
            })
            {
                data.Add(method, path, configuration);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(DomainCases))]
    public async Task EscapedSurrogatesAndDuplicates_KeepDomainOnly422(
        string method, string path, string configuration)
    {
        await Seed(Client);
        var before = await State(Client);
        var error = await Send(new HttpMethod(method), path, Envelope(method, configuration), 422);
        Problem(error, 422, JsonValueKind.String);
        error.GetProperty("detail").GetString().ShouldBe("JSON data is not valid.");
        (await State(Client)).ShouldBe(before);
    }

    [Theory]
    [InlineData("POST", "/v3/workspaces")]
    [InlineData("PUT", W)]
    [InlineData("POST", W + "/sessions")]
    [InlineData("PUT", S)]
    public async Task TypedConfigurationErrors_KeepTheirNestedLocation(string method, string path)
    {
        await Seed(Client);
        var before = await State(Client);
        var error = await Send(new HttpMethod(method), path,
            Envelope(method, """{"summary":{"enabled":"no"}}"""), 422);
        Location(error, "body", "configuration", "summary", "enabled");
        error.GetProperty("detail")[0].GetProperty("type").GetString().ShouldBe("value_error");
        (await State(Client)).ShouldBe(before);
    }

    [Theory]
    [InlineData("POST", "/v3/workspaces")]
    [InlineData("PUT", W)]
    [InlineData("POST", W + "/sessions")]
    [InlineData("PUT", S)]
    public async Task UnknownRootValue_IsNotSubjectToNewUtf8Scanning(string method, string path)
    {
        await Seed(Client);
        var body = Envelope(method, """{"custom_instructions":"accepted"}""");
        var (status, text) = await Raw(Client, method, path,
            Payload(body[..^1] + ""","future":"@@"}""", [0xFF]));
        status.ShouldBe(200, text);
        using var document = JsonDocument.Parse(text);
        document.RootElement.GetProperty("configuration").GetProperty("custom_instructions").GetString().ShouldBe("accepted");
    }

    private static string Envelope(string method, string configuration) =>
        "{" + (method == "POST" ? "\"id\":\"new\",\"peers\":{\"not-stored\":{}}," : "") +
        "\"metadata\":{\"state\":\"not-stored\"},\"configuration\":" + configuration + "}";

    private static byte[] Payload(string template, byte[] bytes)
    {
        var parts = template.Split("@@", StringSplitOptions.None);
        parts.Length.ShouldBe(2);
        return [.. Encoding.ASCII.GetBytes(parts[0]), .. bytes, .. Encoding.ASCII.GetBytes(parts[1])];
    }

    private static async Task<(int Status, string Body)> Raw(HttpClient client, string method, string path, byte[] body)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> PostOk(HttpClient client, string path, string body)
    {
        var (status, text) = await Raw(client, "POST", path, Encoding.UTF8.GetBytes(body));
        status.ShouldBe(200, text);
        return text;
    }

    private static async Task Seed(HttpClient client)
    {
        await PostOk(client, "/v3/workspaces",
            """{"id":"w","metadata":{"state":"original"},"configuration":{"custom_instructions":"original"}}""");
        await PostOk(client, W + "/sessions",
            """{"id":"s","metadata":{"state":"original"},"configuration":{"custom_instructions":"original"}}""");
    }

    private static async Task<string[]> State(HttpClient client) =>
    [
        await PostOk(client, "/v3/workspaces/list", "{}"),
        await PostOk(client, W + "/sessions/list", "{}"),
        await PostOk(client, W + "/peers/list", "{}"),
    ];

    private sealed class ExceptionObserver : IExceptionHandler
    {
        public ConcurrentQueue<Exception> Exceptions { get; } = new();
        public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            Exceptions.Enqueue(exception);
            return ValueTask.FromResult(false);
        }
    }

    private sealed class ErrorLog : ILoggerProvider
    {
        public ConcurrentQueue<string> Events { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(this);
        public void Dispose() { }

        private sealed class Capture(ErrorLog owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    owner.Events.Enqueue(formatter(state, exception) + Environment.NewLine + exception);
                }
            }
        }
    }
}
