using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Core.Configuration;
using Nachos.Core.Keys;
using Nachos.Core.Tokens;
using Nachos.Core.Validation;
using NSubstitute;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class ServiceBoundaryTests
{
    [Theory]
    [InlineData("workspace create")]
    [InlineData("workspace update")]
    [InlineData("peer create")]
    [InlineData("peer update")]
    [InlineData("peer list")]
    [InlineData("peer sessions")]
    [InlineData("session create")]
    [InlineData("session update")]
    [InlineData("session list")]
    [InlineData("members add")]
    [InlineData("members set")]
    [InlineData("members remove")]
    [InlineData("members list")]
    [InlineData("config get")]
    [InlineData("config set")]
    [InlineData("messages list")]
    [InlineData("message get")]
    [InlineData("message update")]
    [InlineData("messages create")]
    public async Task InvalidTargetIds_AreRejectedBeforeStoreAccess(string operation)
    {
        var f = new Fixture();
        await Should.ThrowAsync<NachosValidationException>(() => operation switch
        {
            "workspace create" => f.Service.GetOrCreateWorkspaceAsync("bad space"),
            "workspace update" => f.Service.UpdateWorkspaceAsync("bad space"),
            "peer create" => f.Service.GetOrCreatePeerAsync("W", "bad space"),
            "peer update" => f.Service.UpdatePeerAsync("W", "bad space"),
            "peer list" => f.Service.ListPeersAsync("bad space", null, null, new()),
            "peer sessions" => f.Service.ListPeerSessionsAsync("W", "bad space", null, new()),
            "session create" => f.Service.GetOrCreateSessionAsync("W", "bad space"),
            "session update" => f.Service.UpdateSessionAsync("W", "bad space"),
            "session list" => f.Service.ListSessionsAsync("bad space", null, new()),
            "members add" => f.Service.AddSessionPeersAsync("W", "bad space", new Dictionary<string, SessionPeerConfig>()),
            "members set" => f.Service.SetSessionPeersAsync("W", "bad space", new Dictionary<string, SessionPeerConfig>()),
            "members remove" => f.Service.RemoveSessionPeersAsync("W", "bad space", []),
            "members list" => f.Service.ListSessionPeersAsync("W", "bad space", new()),
            "config get" => f.Service.GetSessionPeerConfigAsync("W", "S", "bad space"),
            "config set" => f.Service.SetSessionPeerConfigAsync("W", "S", "bad space", new()),
            "messages list" => f.Service.ListMessagesAsync("W", "bad space", null, new()),
            "message get" => f.Service.GetMessageAsync("W", "S", "bad space"),
            "message update" => f.Service.UpdateMessageAsync("W", "S", "bad space", null),
            "messages create" => f.Service.CreateMessagesAsync("bad space", "S", [new("ok", "P")]),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        });
        f.Store.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("workspace create")]
    [InlineData("workspace update")]
    [InlineData("peer create")]
    [InlineData("peer update")]
    [InlineData("peer configuration")]
    [InlineData("session create")]
    [InlineData("session update")]
    [InlineData("message update")]
    [InlineData("workspaces filter")]
    [InlineData("peers filter")]
    [InlineData("peer sessions filter")]
    [InlineData("sessions filter")]
    [InlineData("messages filter")]
    public async Task EveryConstructedJsonIngress_RejectsOpaqueDataWithoutGetters(string operation)
    {
        var f = new Fixture();
        var opaque = new Opaque();
        var json = new JsonObject { ["unknown"] = JsonValue.Create(opaque) };
        await Should.ThrowAsync<NachosValidationException>(() => operation switch
        {
            "workspace create" => f.Service.GetOrCreateWorkspaceAsync("W", json),
            "workspace update" => f.Service.UpdateWorkspaceAsync("W", json),
            "peer create" => f.Service.GetOrCreatePeerAsync("W", "P", json),
            "peer update" => f.Service.UpdatePeerAsync("W", "P", json),
            "peer configuration" => f.Service.GetOrCreatePeerAsync("W", "P", configuration: json),
            "session create" => f.Service.GetOrCreateSessionAsync("W", "S", json),
            "session update" => f.Service.UpdateSessionAsync("W", "S", json),
            "message update" => f.Service.UpdateMessageAsync("W", "S", "M", json),
            "workspaces filter" => f.Service.ListWorkspacesAsync(json, new()),
            "peers filter" => f.Service.ListPeersAsync("W", null, json, new()),
            "peer sessions filter" => f.Service.ListPeerSessionsAsync("W", "P", json, new()),
            "sessions filter" => f.Service.ListSessionsAsync("W", json, new()),
            "messages filter" => f.Service.ListMessagesAsync("W", "S", json, new()),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        });
        opaque.GetterCalls.ShouldBe(0);
        f.Store.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task InvalidKindsAndMemberCollections_DoNotReachStores()
    {
        var f = new Fixture();
        var peers = new Dictionary<string, SessionPeerConfig> { ["P"] = new(), ["bad space"] = new() };
        await Should.ThrowAsync<NachosValidationException>(() => f.Service.ListPeersAsync("W", (PeerKind)999, null, new()));
        await Should.ThrowAsync<NachosValidationException>(() => f.Service.AddSessionPeersAsync("W", "S", peers));
        await Should.ThrowAsync<NachosValidationException>(() => f.Service.SetSessionPeersAsync("W", "S", peers));
        await Should.ThrowAsync<NachosValidationException>(() => f.Service.RemoveSessionPeersAsync("W", "S", ["P", "bad space"]));
        await Should.ThrowAsync<NachosValidationException>(() => f.Service.AddSessionPeersAsync("W", "S",
            new Dictionary<string, SessionPeerConfig> { ["P"] = null! }));
        f.Store.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task PagingConstructorsRejectInvalidValuesBeforeServiceCalls()
    {
        var f = new Fixture();
        await Should.ThrowAsync<RequestValidationException>(() => f.Service.ListWorkspacesAsync(null, new(0)));
        await Should.ThrowAsync<RequestValidationException>(() => f.Service.ListSessionsAsync("W", null, new(Size: 101)));
        f.Store.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task TypedConfigurationProjection_KeepsEveryFieldAndNull()
    {
        var f = new Fixture();
        JsonObject? captured = null;
        f.Store.Workspaces.GetOrCreateAsync("W", Arg.Is<JsonObject?>(m => m == null), Arg.Any<JsonObject?>(), default).Returns(info =>
        {
            captured = info.ArgAt<JsonObject>(2);
            return new WorkspaceRecord("W", new(), captured, LifecycleState.Active, DateTimeOffset.UnixEpoch);
        });
        var configuration = new WorkspaceConfiguration(new(false, "r"), new(true, false, "p"),
            new(true, 10, 20, "s"), new(false, "d"), new("x"), "");

        await f.Service.GetOrCreateWorkspaceAsync("W", configuration: configuration);

        JsonNode.DeepEquals(captured, JsonNode.Parse("""
            {"reasoning":{"enabled":false,"custom_instructions":"r"},
             "peer_card":{"use":true,"create":false,"custom_instructions":"p"},
             "summary":{"enabled":true,"messages_per_short_summary":10,"messages_per_long_summary":20,"custom_instructions":"s"},
             "dream":{"enabled":false,"custom_instructions":"d"},"dialectic":{"custom_instructions":"x"},"custom_instructions":""}
            """)).ShouldBeTrue();
    }

    [Fact]
    public async Task CancellationDuringStoreCall_IsPropagatedWithTheOriginalToken()
    {
        var f = new Fixture();
        using var cts = new CancellationTokenSource();
        f.Store.Workspaces.GetOrCreateAsync("W", null, null, cts.Token).Returns(_ =>
        {
            cts.Cancel();
            return Task.FromCanceled<WorkspaceRecord>(cts.Token);
        });

        var error = await Should.ThrowAsync<OperationCanceledException>(() => f.Service.GetOrCreateWorkspaceAsync("W", ct: cts.Token));
        error.CancellationToken.ShouldBe(cts.Token);
    }

    [Fact]
    public void RepeatedRegistration_OneCompleteClientPerScope_NoImplicitProvider()
    {
        var services = new ServiceCollection();
        services.AddNachos(_ => { }).AddNachos(_ => { });
        services.Count(d => d.ServiceType == typeof(INachosClient)).ShouldBe(1);
        services.Count(d => d.ServiceType == typeof(NachosService)).ShouldBe(1);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Should.Throw<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<INachosClient>());
    }

    private sealed class Fixture
    {
        public IMemoryStore Store { get; } = Substitute.For<IMemoryStore>();
        public NachosService Service { get; }
        public Fixture()
        {
            var counter = Substitute.For<ITokenCounter>();
            Service = new(Store, counter, new RequestValidator(Options.Create(new NachosOptions()), counter), Substitute.For<IKeyIssuer>());
        }
    }

    private sealed class Opaque
    {
        public int GetterCalls { get; private set; }
        public string Value { get { GetterCalls++; return "not JSON"; } }
    }
}
