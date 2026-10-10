using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Nachos.Core;
using Shouldly;

namespace Nachos.Api.Tests.Auth;

public sealed class AdmissionReplayTests
{
    private const string Messages = "/v3/workspaces/A/sessions/s1/messages";
    private static string Envelope(int depth) => "{\"messages\":[{\"content\":\"captured\",\"peer_id\":\"p1\"}],\"future\":" +
        new string('[', depth - 1) + "0" + new string(']', depth - 1) + "}";

    [Theory]
    [InlineData(999)]
    [InlineData(1000)]
    public async Task CoreCapturedDeepEnvelope_HttpReplayPreservesStatusBodyAndIdentity(int depth)
    {
        CountingStore? counted = null;
        using var host = Host(store => counted = store);
        await host.Seed();
        var envelope = Envelope(depth);
        using var document = JsonDocument.Parse(envelope, new JsonDocumentOptions { MaxDepth = int.MaxValue });
        using var scope = host.Services.CreateScope();
        var captured = await scope.ServiceProvider.GetRequiredService<NachosService>()
            .CreateMessagesResponseAsync("A", "s1", document.RootElement, "deep-replay", default);
        var store = counted.ShouldNotBeNull();
        var before = (await store.Inner.Idempotency.TryGetAsync("A", "deep-replay", default)).ShouldNotBeNull();
        store.Reads = 0;
        store.Appends = 0;
        using var request = Request(envelope, AuthHost.Key("session"), "deep-replay");
        using var response = await host.Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        captured.Status.ShouldBe(201);
        text.ShouldBe(captured.Body);
        ((int)response.StatusCode).ShouldBe(captured.Status);
        store.Reads.ShouldBeGreaterThan(0);
        store.Appends.ShouldBe(0);
        (await store.Inner.Idempotency.TryGetAsync("A", "deep-replay", default)).ShouldBe(before);
        var messages = await host.Send("POST", Messages + "/list", "{}", AuthHost.Key("session"), 200);
        messages.GetProperty("total").GetInt32().ShouldBe(1);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(1000)]
    public async Task ExistingValidCapturedKey_Depth1001CannotBypassHttpAdmission(int capturedDepth)
    {
        CountingStore? counted = null;
        using var host = Host(store => counted = store);
        await host.Seed();
        var validEnvelope = Envelope(capturedDepth);
        using var document = JsonDocument.Parse(validEnvelope, new JsonDocumentOptions { MaxDepth = int.MaxValue });
        using var scope = host.Services.CreateScope();
        var captured = await scope.ServiceProvider.GetRequiredService<NachosService>()
            .CreateMessagesResponseAsync("A", "s1", document.RootElement, "existing-valid-key", default);
        captured.Status.ShouldBe(201);
        var store = counted.ShouldNotBeNull();
        store.Appends.ShouldBe(1);
        var before = (await store.Inner.Idempotency.TryGetAsync("A", "existing-valid-key", default)).ShouldNotBeNull();
        before.ResponseBody.ShouldBe(captured.Body);
        var messagesBefore = await ReadMessagesAsync(host);
        var sessionBefore = JsonSerializer.Serialize(await store.Inner.Sessions.GetAsync("A", "s1", default));
        store.Reads = 0;
        store.Appends = 0;

        // Only the valid envelope was captured. The overlimit request reuses its existing workspace/key.
        using var request = Request(Envelope(1001), AuthHost.Key("session"), "existing-valid-key");
        using var response = await host.Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).ShouldBe(422, text);
        AssertDepthError(text);
        store.Reads.ShouldBe(0);
        store.Appends.ShouldBe(0);
        (await store.Inner.Idempotency.TryGetAsync("A", "existing-valid-key", default)).ShouldBe(before);
        (await ReadMessagesAsync(host)).ShouldBe(messagesBefore);
        JsonSerializer.Serialize(await store.Inner.Sessions.GetAsync("A", "s1", default)).ShouldBe(sessionBefore);

        using var control = Request(validEnvelope, AuthHost.Key("session"), "existing-valid-key");
        using var replay = await host.Http.SendAsync(control);
        ((int)replay.StatusCode).ShouldBe(captured.Status);
        (await replay.Content.ReadAsByteArrayAsync()).ShouldBe(Encoding.UTF8.GetBytes(captured.Body));
        store.Reads.ShouldBeGreaterThan(0);
        store.Appends.ShouldBe(0);
        (await store.Inner.Idempotency.TryGetAsync("A", "existing-valid-key", default)).ShouldBe(before);
    }

    [Fact]
    public async Task BaselineCoreWriter_CannotCaptureKeyedDepth1001BeforeLookupOrAppend()
    {
        CountingStore? counted = null;
        using var host = Host(store => counted = store);
        await host.Seed();
        var store = counted.ShouldNotBeNull();
        var messagesBefore = await ReadMessagesAsync(host);
        var sessionBefore = JsonSerializer.Serialize(await store.Inner.Sessions.GetAsync("A", "s1", default));
        using var document = JsonDocument.Parse(Envelope(1001), new JsonDocumentOptions { MaxDepth = int.MaxValue });
        using var scope = host.Services.CreateScope();
        store.Reads = 0;
        store.Appends = 0;

        // Characterize the supplied Core writer, not a new limit or an HTTP admission substitute.
        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<NachosService>()
                .CreateMessagesResponseAsync("A", "s1", document.RootElement, "impossible-capture", default));
        error.Message.ShouldContain("maximum allowed depth of 1000");
        error.StackTrace.ShouldNotBeNull().ShouldContain("Nachos.Core.Idempotency.CanonicalJson");
        store.Reads.ShouldBe(0);
        store.Appends.ShouldBe(0);
        (await store.Inner.Idempotency.TryGetAsync("A", "impossible-capture", default)).ShouldBeNull();
        (await ReadMessagesAsync(host)).ShouldBe(messagesBefore);
        JsonSerializer.Serialize(await store.Inner.Sessions.GetAsync("A", "s1", default)).ShouldBe(sessionBefore);
    }

    private static async Task<string> ReadMessagesAsync(AuthHost host) =>
        (await host.Send("POST", Messages + "/list", "{}", AuthHost.Key("session"), 200)).GetRawText();

    [Theory]
    [InlineData("none")]
    [InlineData("other")]
    [InlineData("peer")]
    [InlineData("revoked")]
    public async Task PopulatedRealReplay_DenialDoesNotReadRecord(string identity)
    {
        CountingStore? counted = null;
        using var entra = new OfflineEntra();
        using var host = Host(store => counted = store, entra);
        await host.Seed();
        var grant = new GrantRecord("object-id", "A", GrantRoles.Workspace);
        await host.Store.Grants.AddAsync(grant, default);
        var entraToken = entra.Token();
        var body = Envelope(4);
        using var original = Request(body, entraToken, "populated");
        using var appended = await host.Http.SendAsync(original);
        appended.StatusCode.ShouldBe(HttpStatusCode.Created);
        var bytes = await appended.Content.ReadAsByteArrayAsync();
        var store = counted.ShouldNotBeNull();
        var before = (await store.Inner.Idempotency.TryGetAsync("A", "populated", default)).ShouldNotBeNull();
        if (identity == "revoked") await host.Store.Grants.RemoveAsync(grant, default);
        var token = identity switch { "none" => null, "revoked" => entraToken, _ => AuthHost.Key(identity) };
        store.Reads = 0;
        using var denied = Request(body, token, "populated");
        using var response = await host.Http.SendAsync(denied);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        store.Reads.ShouldBe(0);
        (await store.Inner.Idempotency.TryGetAsync("A", "populated", default)).ShouldBe(before);
        using var control = Request(body, AuthHost.Key("session"), "populated");
        using var replay = await host.Http.SendAsync(control);
        replay.StatusCode.ShouldBe(appended.StatusCode);
        (await replay.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);
        store.Reads.ShouldBeGreaterThan(0);
        (await host.Send("POST", Messages + "/list", "{}", AuthHost.Key("session"), 200))
            .GetProperty("total").GetInt32().ShouldBe(1);
    }

    private static readonly int[] Depths = [999, 1000, 1001];
    public static IEnumerable<object[]> BodyPaths() =>
        from route in ScopeMatrixTests.Implemented.Where(route => route.Body is not null && route.Method != "DELETE")
        from depth in Depths
        select new object[] { route.Method, route.Template, depth };

    [Theory]
    [MemberData(nameof(BodyPaths))]
    public async Task EveryObjectParser_EnforcesHttpDepth(string method, string template, int depth)
    {
        var route = ScopeMatrixTests.Implemented.Single(route => route.Method == method && route.Template == template);
        using var host = new AuthHost();
        await host.Seed();
        var messages = await host.Send("POST", Messages, Envelope(4), AuthHost.Key("admin"), 201);
        var messageId = messages[0].GetProperty("id").GetString()!;
        var nested = new string('[', depth - 1) + "0" + new string(']', depth - 1);
        var body = route.Body![..^1] + (route.Body.Length > 2 ? "," : "") + "\"future\":" + nested + "}";
        // Peer-map routes accept arbitrary peer names, not arbitrary root fields. Put the ignored field in a config.
        if (template == ScopeMatrixTests.S + "/peers")
            body = "{\"p1\":{\"future\":" + new string('[', depth - 2) + "0" + new string(']', depth - 2) + "}}";
        var result = await host.Send(method, ScopeMatrixTests.Concrete(template, messageId), body,
            AuthHost.Key(template == "/v3/workspaces" ? "workspace" : "admin"), depth > 1000 ? 422 : route.Status);
        if (depth > 1000) AssertDepthError(result.GetRawText());
    }

    [Fact]
    public async Task NonObjectParser_AlsoRejectsOverflowBeforeCoreValidation()
    {
        using var host = new AuthHost();
        await host.Seed();
        var body = new string('[', 1001) + "0" + new string(']', 1001);
        var error = await host.Send("DELETE", ScopeMatrixTests.Concrete(ScopeMatrixTests.S + "/peers"), body, AuthHost.Key("session"), 422);
        AssertDepthError(error.GetRawText());
        (await host.Store.Sessions.IsActiveMemberAsync("A", "s1", "p1", default)).ShouldBeTrue();
    }

    [Fact]
    public async Task BomWorkspaceBody_IsRewoundWithoutLosingMetadata()
    {
        using var host = new AuthHost();
        var payload = Encoding.UTF8.GetBytes("\uFEFF{\"id\":\"A\",\"metadata\":{\"label\":\"caf\\u00e9\"}}");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v3/workspaces") { Content = new ByteArrayContent(payload) };
        request.Headers.Authorization = new("Bearer", AuthHost.Key("workspace"));
        request.Content.Headers.ContentType = new("application/json");
        using var response = await host.Http.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        result.RootElement.GetProperty("metadata").GetProperty("label").GetString().ShouldBe("caf\u00e9");
    }

    internal static void AssertDepthError(string text)
    {
        using var error = JsonDocument.Parse(text);
        var detail = error.RootElement.GetProperty("detail")[0];
        detail.GetProperty("loc").EnumerateArray().Select(value => value.GetString()).ShouldBe(["body"]);
        detail.GetProperty("type").GetString().ShouldBe("json_invalid");
    }

    private static HttpRequestMessage Request(string body, string? token, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Messages + "/")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static AuthHost Host(Action<CountingStore> capture, OfflineEntra? entra = null) => new()
    {
        Entra = entra,
        ServicesOverride = services =>
        {
            var descriptor = services.Last(service => service.ServiceType == typeof(IMemoryStore));
            services.Remove(descriptor);
            services.AddSingleton<IMemoryStore>(provider =>
            {
                var inner = (IMemoryStore)(descriptor.ImplementationInstance ??
                    descriptor.ImplementationFactory?.Invoke(provider) ??
                    ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!));
                var counted = new CountingStore(inner);
                capture(counted);
                return counted;
            });
        },
    };

    private sealed class CountingStore(IMemoryStore inner) : IMemoryStore, IIdempotencyStore, IMessageStore
    {
        internal IMemoryStore Inner => inner;
        internal int Reads { get; set; }
        internal int Appends { get; set; }
        public IWorkspaceStore Workspaces => inner.Workspaces;
        public IPeerStore Peers => inner.Peers;
        public ISessionStore Sessions => inner.Sessions;
        public IMessageStore Messages => this;
        public IGrantStore Grants => inner.Grants;
        public IIdempotencyStore Idempotency => this;
        public Task<IdempotencyRecord?> TryGetAsync(string workspaceName, string key, CancellationToken ct)
        {
            Reads++;
            return inner.Idempotency.TryGetAsync(workspaceName, key, ct);
        }

        public Task<IReadOnlyList<MessageRecord>> AppendAsync(string workspaceName, string sessionName,
            IReadOnlyList<NewMessage> messages, IdempotencyWrite? idempotency, CancellationToken ct)
        {
            Appends++;
            return inner.Messages.AppendAsync(workspaceName, sessionName, messages, idempotency, ct);
        }

        public Task<MessageRecord?> GetAsync(string workspaceName, string sessionName, string publicId, CancellationToken ct) =>
            inner.Messages.GetAsync(workspaceName, sessionName, publicId, ct);

        public Task<MessageRecord> UpdateMetadataAsync(string workspaceName, string sessionName, string publicId,
            JsonObject metadata, CancellationToken ct) =>
            inner.Messages.UpdateMetadataAsync(workspaceName, sessionName, publicId, metadata, ct);

        public Task<Page<MessageRecord>> ListAsync(string workspaceName, string sessionName, FilterNode? filter,
            PageRequest page, CancellationToken ct) =>
            inner.Messages.ListAsync(workspaceName, sessionName, filter, page, ct);
    }
}
