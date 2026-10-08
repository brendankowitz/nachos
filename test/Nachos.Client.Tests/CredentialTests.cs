using System.Collections;
using System.Net;
using Azure.Core;
using Nachos.Abstractions;
using Shouldly;

namespace Nachos.Client.Tests;

/// <summary>
/// <see cref="NachosClientOptions.Credential"/>: an Entra <see cref="TokenCredential"/> is preferred over the API key,
/// asked for a token per call, never wrapped when it fails, and its token never reaches exception text or data.
/// </summary>
public sealed class CredentialTests
{
    private const string Token = "eyJ0eXAi.CANARY-TOKEN-7f3a9c.sig";

    private const string ApiKey = "nk-CANARY-APIKEY-41d2";

    private static readonly Uri Base = new("https://nachos.test/");

    private static readonly string[] Scopes = ["api://nachos/.default"];

    private const string MessageJson =
        """{"id":"m1","content":"hi","peer_id":"alice","session_id":"s1","metadata":{},"created_at":"2026-10-08T12:00:00Z","workspace_id":"w1","token_count":1}""";

    [Fact]
    public async Task Credential_IsSentAsBearer_AndIsPreferredOverTheApiKey()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, MessageJson));
        var credential = new FakeCredential(Token);

        await Client(stub, credential, apiKey: ApiKey).GetMessageAsync("w1", "s1", "m1");

        stub.Requests.Single().Authorization.ShouldBe("Bearer " + Token);
    }

    [Fact]
    public async Task Credential_IsAskedForTheConfiguredScopes_OncePerCall_WithTheCallersToken()
    {
        using var cts = new CancellationTokenSource();
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, MessageJson));
        var credential = new FakeCredential(Token);
        var client = Client(stub, credential, scopes: ["api://nachos/.default", "openid"]);

        await client.GetMessageAsync("w1", "s1", "m1", cts.Token);
        await client.GetMessageAsync("w1", "s1", "m1");

        credential.Requests.Count.ShouldBe(2);
        credential.Requests.ShouldAllBe(r => r.Context.Scopes.SequenceEqual(new[] { "api://nachos/.default", "openid" }));
        credential.Requests[0].Token.ShouldBe(cts.Token);
    }

    [Fact]
    public async Task ScopesChangedAfterConstruction_AreNotSeen()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, MessageJson));
        var credential = new FakeCredential(Token);
        var options = new NachosClientOptions { BaseAddress = Base, Credential = credential, Scopes = ["api://nachos/.default"] };
        var client = new NachosHttpClient(new HttpClient(stub), options);
        options.Scopes[0] = "api://other/.default";

        await client.GetMessageAsync("w1", "s1", "m1");

        credential.Requests.Single().Context.Scopes.ShouldBe(["api://nachos/.default"]);
    }

    [Fact]
    public async Task OneTokenServesEveryRetryOfOneCall()
    {
        var stub = new StubHandler((_, attempt) => attempt == 1
            ? StubHandler.Json(HttpStatusCode.ServiceUnavailable, """{"detail":"busy"}""")
            : StubHandler.Json(HttpStatusCode.OK, MessageJson));
        var credential = new FakeCredential(Token);
        var retry = new RetryHandler(TimeProvider.System, (_, _) => Task.CompletedTask, () => 0) { InnerHandler = stub };

        await Client(retry, credential).GetMessageAsync("w1", "s1", "m1");

        credential.Requests.Count.ShouldBe(1);
        stub.Requests.Count.ShouldBe(2);
        stub.Requests.ShouldAllBe(r => r.Authorization == "Bearer " + Token);
    }

    [Fact]
    public async Task CredentialFailure_PropagatesUnchanged_AndNothingIsSent()
    {
        var failure = new InvalidOperationException("credential unavailable");
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, MessageJson));
        var credential = new FakeCredential(_ => throw failure);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Client(stub, credential, apiKey: ApiKey).GetMessageAsync("w1", "s1", "m1"));

        ex.ShouldBeSameAs(failure);
        ex.Data.Count.ShouldBe(0);
        stub.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task CredentialFailure_IsNotRetried()
    {
        var calls = 0;
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, MessageJson));
        var credential = new FakeCredential(_ =>
        {
            calls++;
            throw new HttpRequestException("token endpoint unreachable");
        });
        var retry = new RetryHandler(TimeProvider.System, (_, _) => Task.CompletedTask, () => 0) { InnerHandler = stub };

        await Should.ThrowAsync<HttpRequestException>(() => Client(retry, credential).GetMessageAsync("w1", "s1", "m1"));

        calls.ShouldBe(1);
        stub.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task CredentialCancellation_PropagatesUnchanged()
    {
        using var cts = new CancellationTokenSource();
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, MessageJson));
        var credential = new FakeCredential(ct =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Token;
        });

        await Should.ThrowAsync<OperationCanceledException>(() => Client(stub, credential).GetMessageAsync("w1", "s1", "m1", cts.Token));

        stub.Requests.ShouldBeEmpty();
    }

    /// <summary>A server (or proxy) that echoes the bearer token back must not leak it into exception text or data.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task EchoedToken_NeverAppearsInExceptionTextOrData(HttpStatusCode status)
    {
        var stub = new StubHandler((_, _) =>
        {
            var response = StubHandler.Json(status, $$"""{"detail":"invalid token Bearer {{Token}}"}""");
            response.Headers.TryAddWithoutValidation("Retry-After", "40");
            return response;
        });

        var ex = await Should.ThrowAsync<Exception>(() => Client(stub, new FakeCredential(Token), apiKey: ApiKey).GetMessageAsync("w1", "s1", "m1"));

        AssertNoSecret(ex);
        ex.Message.ShouldContain(ErrorMapper.Redacted);
    }

    [Fact]
    public async Task EchoedToken_NeverAppearsInValidationErrors()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(
            HttpStatusCode.UnprocessableEntity,
            $$"""{"detail":[{"loc":["header","{{Token}}",0],"msg":"bad token {{Token}}","type":"t-{{Token}}"}]}"""));

        var ex = await Should.ThrowAsync<RequestValidationException>(
            () => Client(stub, new FakeCredential(Token)).GetMessageAsync("w1", "s1", "m1"));

        AssertNoSecret(ex);
        ex.Errors.ShouldAllBe(e => !e.Msg.Contains(Token) && !e.Type.Contains(Token) && !e.Loc.OfType<string>().Any(p => p.Contains(Token)));
    }

    [Fact]
    public async Task TransportFailure_WithACredential_DoesNotCarryTheToken()
    {
        var stub = new StubHandler((_, _) => throw new HttpRequestException("connection refused"));

        var ex = await Should.ThrowAsync<HttpRequestException>(() => Client(stub, new FakeCredential(Token)).GetMessageAsync("w1", "s1", "m1"));

        AssertNoSecret(ex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("tok en")]
    [InlineData("tok\r\nX-Injected: 1")]
    [InlineData("tök")]
    public async Task MalformedToken_IsRejected_WithoutEchoingIt_AndNothingIsSent(string token)
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, MessageJson));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Client(stub, new FakeCredential(token)).GetMessageAsync("w1", "s1", "m1"));

        if (token.Length > 0)
        {
            ex.ToString().ShouldNotContain(token);
        }

        stub.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void CredentialWithoutScopes_IsRejected()
    {
        var ex = Should.Throw<ArgumentException>(() => new NachosHttpClient(
            new HttpClient(), new NachosClientOptions { BaseAddress = Base, Credential = new FakeCredential(Token), Scopes = [] }));

        ex.Message.ShouldContain("Scopes");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void CredentialWithABlankScope_IsRejected(string scope)
    {
        Should.Throw<ArgumentException>(() => new NachosHttpClient(
            new HttpClient(), new NachosClientOptions { BaseAddress = Base, Credential = new FakeCredential(Token), Scopes = ["api://nachos/.default", scope] }));
    }

    [Fact]
    public void CredentialWithNullScopes_IsRejected()
    {
        Should.Throw<ArgumentException>(() => new NachosHttpClient(
            new HttpClient(), new NachosClientOptions { BaseAddress = Base, Credential = new FakeCredential(Token), Scopes = null! }));
    }

    [Fact]
    public void Options_ToString_PrintsNoSecret()
    {
        var options = new NachosClientOptions { BaseAddress = Base, ApiKey = ApiKey, Credential = new FakeCredential(Token), Scopes = Scopes };

        options.ToString()!.ShouldNotContain(ApiKey);
        options.ToString()!.ShouldNotContain(Token);
    }

    private static NachosHttpClient Client(HttpMessageHandler pipeline, TokenCredential credential, string? apiKey = null, string[]? scopes = null) =>
        new(new HttpClient(pipeline), new NachosClientOptions { BaseAddress = Base, ApiKey = apiKey, Credential = credential, Scopes = scopes ?? Scopes });

    private static void AssertNoSecret(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            current.ToString().ShouldNotContain(Token);
            current.ToString().ShouldNotContain(ApiKey);
            foreach (DictionaryEntry entry in current.Data)
            {
                $"{entry.Key}={entry.Value}".ShouldNotContain(Token);
                $"{entry.Key}={entry.Value}".ShouldNotContain(ApiKey);
            }

            if (current is NachosValidationException validation)
            {
                validation.Detail.ShouldNotContain(Token);
            }
        }
    }

    /// <summary>Hands out a token per call and records each request; the synchronous path must never be used.</summary>
    private sealed class FakeCredential(Func<CancellationToken, string> token) : TokenCredential
    {
        public FakeCredential(string token)
            : this(_ => token)
        {
        }

        public List<(TokenRequestContext Context, CancellationToken Token)> Requests { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The client must acquire tokens asynchronously.");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Requests.Add((requestContext, cancellationToken));
            return ValueTask.FromResult(new AccessToken(token(cancellationToken), DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
}
