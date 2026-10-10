using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Nachos.Core.Configuration;
using Nachos.Core.Keys;
using Nachos.Core.Tokens;
using Nachos.Core.Validation;
using NSubstitute;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class NachosServiceTests
{
    private readonly IMemoryStore _store = Substitute.For<IMemoryStore>();
    private readonly IKeyIssuer _issuer = Substitute.For<IKeyIssuer>();
    private readonly ITokenCounter _tokens = Substitute.For<ITokenCounter>();
    private readonly CancellationToken _ct = new CancellationTokenSource().Token;
    private static readonly DateTimeOffset Created = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private NachosService Service => new(_store, _tokens,
        new RequestValidator(Options.Create(new NachosOptions()), _tokens), _issuer);

    private static WorkspaceRecord WorkspaceRow() =>
        new("W", new() { ["m"] = 1 }, new() { ["c"] = 2 }, LifecycleState.Active, Created);
    private static PeerRecord PeerRow() => new("W", "P", new() { ["m"] = 1 }, new(), true, Created);
    private static SessionRecord SessionRow(LifecycleState state = LifecycleState.Active) =>
        new("W", "S", state, new() { ["m"] = 1 }, new(), Created);
    private static MessageRecord MessageRow() => new("Public-A", "W", "S", "P", 99, "hello", 7, new() { ["m"] = 1 }, Created);
    private static Page<T> PageOf<T>(T row) => new([row], 12, 2, 3, 4);

    [Fact]
    public async Task WorkspaceCreateAndUpdate_MapFieldsAndPreserveNullUpdates()
    {
        var row = WorkspaceRow();
        _store.Workspaces.GetOrCreateAsync("W", Arg.Any<JsonObject?>(), Arg.Any<JsonObject?>(), _ct).Returns(row);
        _store.Workspaces.UpdateAsync("W", null, null, _ct).Returns(row);
        var metadata = new JsonObject { ["literal"] = 'x' };
        var config = new WorkspaceConfiguration(Reasoning: new(false, "reason"));

        var created = await Service.GetOrCreateWorkspaceAsync("W", metadata, config, _ct);
        var updated = await Service.UpdateWorkspaceAsync("W", ct: _ct);

        created.ShouldBe(new Workspace(row.Name, row.Metadata, row.Configuration, Created));
        updated.ShouldBe(created);
        await _store.Workspaces.Received(1).GetOrCreateAsync("W",
            Arg.Is<JsonObject>(m => !ReferenceEquals(m, metadata) && m["literal"]!.GetValue<string>() == "x"),
            Arg.Is<JsonObject>(c => c["reasoning"]!["enabled"]!.GetValue<bool>() == false), _ct);
        await _store.Workspaces.Received(1).UpdateAsync("W", null, null, _ct);
    }

    [Fact]
    public async Task PeerCreateAndUpdate_KeepCaseAndNeverCreateInternalPeers()
    {
        var row = PeerRow();
        _store.Peers.GetOrCreateAsync("W", "P", Arg.Any<JsonObject?>(), Arg.Any<JsonObject?>(), _ct, false).Returns(row);
        _store.Peers.UpdateAsync("W", "P", null, null, _ct).Returns(row);
        var config = new JsonObject { ["observe_me"] = false, ["future"] = 1 };

        var created = await Service.GetOrCreatePeerAsync("W", "P", configuration: config, ct: _ct);
        var updated = await Service.UpdatePeerAsync("W", "P", ct: _ct);

        created.ShouldBe(new Peer("P", "W", Created, row.Metadata, row.Configuration));
        updated.ShouldBe(created);
        await _store.Peers.Received(1).GetOrCreateAsync("W", "P", Arg.Is<JsonObject?>(m => m == null),
            Arg.Is<JsonObject>(c => !ReferenceEquals(c, config) && c["observe_me"]!.GetValue<bool>() == false), _ct, false);
    }

    [Theory]
    [InlineData(LifecycleState.Active, true)]
    [InlineData(LifecycleState.Inactive, false)]
    [InlineData(LifecycleState.Deleting, false)]
    public async Task SessionCreateAndUpdate_MapStateAndPassMembershipConfiguration(LifecycleState state, bool active)
    {
        var row = SessionRow(state);
        var peers = new Dictionary<string, SessionPeerConfig> { ["P"] = new(false, true) };
        _store.Sessions.GetOrCreateAsync("W", "S", Arg.Is<JsonObject?>(m => m == null), Arg.Any<JsonObject?>(),
            Arg.Any<IReadOnlyDictionary<string, SessionPeerConfig>?>(), _ct).Returns(row);
        _store.Sessions.UpdateAsync("W", "S", null, null, _ct).Returns(row);

        var created = await Service.GetOrCreateSessionAsync("W", "S",
            configuration: new(Reasoning: new(true)), peers: peers, ct: _ct);
        var updated = await Service.UpdateSessionAsync("W", "S", ct: _ct);

        created.ShouldBe(new Session("S", active, "W", row.Metadata, row.Configuration, Created));
        updated.ShouldBe(created);
        await _store.Sessions.Received(1).GetOrCreateAsync("W", "S", Arg.Is<JsonObject?>(m => m == null),
            Arg.Is<JsonObject>(c => c["reasoning"]!["enabled"]!.GetValue<bool>()),
            Arg.Is<IReadOnlyDictionary<string, SessionPeerConfig>>(p => p["P"] == new SessionPeerConfig(false, true)), _ct);
    }

    [Fact]
    public async Task AllLists_PreserveStoreOrderingAndEnvelopeAndParseResourceSpecificFilters()
    {
        var page = new PageRequest(2, 3, true);
        var text = new JsonObject { ["name"] = "W" };
        var peerFilter = new JsonObject { ["peer_id"] = "P" };
        var sessionFilter = new JsonObject { ["is_active"] = false };
        var messageFilter = new JsonObject { ["token_count"] = 7 };
        _store.Workspaces.ListAsync(Arg.Any<FilterNode?>(), page, _ct).Returns(PageOf(WorkspaceRow()));
        _store.Peers.ListAsync("W", PeerKind.Regular, Arg.Any<FilterNode?>(), page, _ct).Returns(PageOf(PeerRow()));
        _store.Sessions.ListAsync("W", Arg.Any<FilterNode?>(), page, _ct).Returns(PageOf(SessionRow()));
        _store.Peers.ListSessionsForPeerAsync("W", "P", Arg.Any<FilterNode?>(), page, _ct).Returns(PageOf(SessionRow()));
        _store.Messages.ListAsync("W", "S", Arg.Any<FilterNode?>(), page, _ct).Returns(PageOf(MessageRow()));
        _store.Sessions.ListPeersAsync("W", "S", page with { Reverse = false }, _ct).Returns(PageOf(PeerRow()));

        CheckPage(await Service.ListWorkspacesAsync(text, page, _ct), "W", w => w.Id);
        CheckPage(await Service.ListPeersAsync("W", null, peerFilter, page, _ct), "P", p => p.Id);
        CheckPage(await Service.ListSessionsAsync("W", sessionFilter, page, _ct), "S", s => s.Id);
        CheckPage(await Service.ListPeerSessionsAsync("W", "P", sessionFilter, page, _ct), "S", s => s.Id);
        CheckPage(await Service.ListMessagesAsync("W", "S", messageFilter, page, _ct), "Public-A", m => m.Id);
        CheckPage(await Service.ListSessionPeersAsync("W", "S", page, _ct), "P", p => p.Id);

        await _store.Messages.Received(1).ListAsync("W", "S",
            Arg.Is<FilterNode>(f => f is FilterNode.Field && ((FilterNode.Field)f).Column == FilterColumns.TokenCount), page, _ct);
        await _store.Workspaces.Received(1).ListAsync(new FilterNode.Field(FilterColumns.Name, FilterOp.Eq, JsonValue.Create("W")), page, _ct);
        await _store.Peers.Received(1).ListAsync("W", PeerKind.Regular, new FilterNode.Field(FilterColumns.Name, FilterOp.Eq, JsonValue.Create("P")), page, _ct);
        await _store.Sessions.Received(1).ListAsync("W", new FilterNode.Field(FilterColumns.IsActive, FilterOp.Eq, JsonValue.Create(false)), page, _ct);
        await _store.Peers.Received(1).ListSessionsForPeerAsync("W", "P", new FilterNode.Field(FilterColumns.IsActive, FilterOp.Eq, JsonValue.Create(false)), page, _ct);
        await _store.Sessions.Received(1).ListPeersAsync("W", "S", page with { Reverse = false }, _ct);
    }

    [Theory]
    [InlineData(PeerKind.Regular)]
    [InlineData(PeerKind.Scope)]
    [InlineData(PeerKind.All)]
    public async Task PeerKinds_AreForwardedWithoutChangingPageDefaults(PeerKind kind)
    {
        _store.Peers.ListAsync("W", kind, null, new PageRequest(), _ct).Returns(PageOf(PeerRow()));

        (await Service.ListPeersAsync("W", kind, null, new(), _ct)).Items.Count.ShouldBe(1);

        await _store.Peers.Received(1).ListAsync("W", kind, null, new(1, 50, false), _ct);
    }

    [Fact]
    public async Task MembershipOperations_UseActiveMemberStoresAndReturnCurrentSession()
    {
        var peers = new Dictionary<string, SessionPeerConfig> { ["P"] = new(false, true) };
        _store.Sessions.GetAsync("W", "S", _ct).Returns(SessionRow());
        _store.Sessions.GetPeerConfigAsync("W", "S", "P", _ct).Returns(new SessionPeerConfig(false, true));

        (await Service.AddSessionPeersAsync("W", "S", peers, _ct)).Id.ShouldBe("S");
        (await Service.SetSessionPeersAsync("W", "S", peers, _ct)).IsActive.ShouldBe(true);
        (await Service.RemoveSessionPeersAsync("W", "S", ["P"], _ct)).Id.ShouldBe("S");
        (await Service.GetSessionPeerConfigAsync("W", "S", "P", _ct)).ShouldBe(new(false, true));
        await Service.SetSessionPeerConfigAsync("W", "S", "P", new(true, false), _ct);

        await _store.Sessions.Received(1).AddPeersAsync("W", "S", Arg.Is<IReadOnlyDictionary<string, SessionPeerConfig>>(p => p["P"] == peers["P"]), _ct);
        await _store.Sessions.Received(1).SetPeersAsync("W", "S", Arg.Is<IReadOnlyDictionary<string, SessionPeerConfig>>(p => p["P"] == peers["P"]), _ct);
        await _store.Sessions.Received(1).RemovePeersAsync("W", "S", Arg.Is<IReadOnlyList<string>>(p => p.Single() == "P"), _ct);
        await _store.Sessions.Received(1).SetPeerConfigAsync("W", "S", "P", new(true, false), _ct);
    }

    [Fact]
    public async Task MessageGetAndUpdate_MapPublicIdAndLeaveNullMetadataUnchanged()
    {
        var row = MessageRow();
        _store.Messages.GetAsync("W", "S", "Public-A", _ct).Returns(row);
        _store.Messages.UpdateMetadataAsync("W", "S", "Public-A", Arg.Any<JsonObject>(), _ct).Returns(row);

        var read = await Service.GetMessageAsync("W", "S", "Public-A", _ct);
        (await Service.UpdateMessageAsync("W", "S", "Public-A", null, _ct)).ShouldBe(read);
        read.ShouldBe(new Message("Public-A", "hello", "P", "S", row.Metadata, Created, "W", 7));
        await _store.Messages.DidNotReceiveWithAnyArgs().UpdateMetadataAsync(default!, default!, default!, default!, default);
        await Service.UpdateMessageAsync("W", "S", "Public-A", new() { ["x"] = 1 }, _ct);
        await _store.Messages.Received(1).UpdateMetadataAsync("W", "S", "Public-A", Arg.Is<JsonObject>(m => m["x"]!.GetValue<int>() == 1), _ct);
    }

    [Fact]
    public async Task MissingMessage_AndStoreNotFoundFailures_AreNotTurnedIntoSuccess()
    {
        await Should.ThrowAsync<NotFoundException>(() => Service.GetMessageAsync("W", "S", "missing", _ct));
        var failure = new NotFoundException("parent missing");
        _store.Peers.GetOrCreateAsync("W", "P", null, null, _ct).Returns(Task.FromException<PeerRecord>(failure));
        (await Should.ThrowAsync<NotFoundException>(() => Service.GetOrCreatePeerAsync("W", "P", ct: _ct))).ShouldBeSameAs(failure);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, "P", null)]
    [InlineData(null, null, "S")]
    [InlineData("W", "P", "S")]
    [InlineData("bad space", null, null)]
    public async Task InvalidKeyScopes_NeverReachIssuer(string? workspace, string? peer, string? session)
    {
        await Should.ThrowAsync<NachosValidationException>(() => Service.CreateKeyAsync(workspace, peer, session, ct: _ct));
        _issuer.DidNotReceiveWithAnyArgs().Issue(default!);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("P", null)]
    [InlineData(null, "S")]
    public async Task TrustedScopedKeyCreation_NeverInfersAdmin(string? peer, string? session)
    {
        _issuer.Issue(Arg.Any<NachosKeyClaims>()).Returns("signed");

        (await Service.CreateKeyAsync("W", peer, session, Created, _ct)).Key.ShouldBe("signed");

        _issuer.Received(1).Issue(new(false, "W", peer, session, Created));
    }

    [Theory]
    [InlineData(GrantRoles.Admin, null)]
    [InlineData(GrantRoles.Workspace, null)]
    [InlineData(GrantRoles.Workspace, "W")]
    public async Task TrustedGrantCreation_UsesExactRoleAndWorkspaceSemantics(string role, string? workspace)
    {
        await Service.AddGrantAsync("object-id", workspace, role, _ct);

        await _store.Grants.Received(1).AddAsync(new("object-id", workspace, role), _ct);
    }

    [Theory]
    [InlineData("", null, GrantRoles.Admin)]
    [InlineData(" ", null, GrantRoles.Admin)]
    [InlineData("oid", null, "admin")]
    [InlineData("oid", "bad space", GrantRoles.Workspace)]
    public async Task InvalidGrants_AreRejectedBeforeStoreCalls(string oid, string? workspace, string role)
    {
        await Should.ThrowAsync<NachosValidationException>(() => Service.AddGrantAsync(oid, workspace, role, _ct));
        _store.Grants.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task InvalidSessionMembership_IsValidatedBeforeCreatingAnything()
    {
        await Should.ThrowAsync<NachosValidationException>(() => Service.GetOrCreateSessionAsync("W", "S",
            peers: new Dictionary<string, SessionPeerConfig> { ["valid"] = new(), ["bad space"] = new() }));
        _store.Sessions.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task InvalidTypedConfigurationAndOpaqueJson_AreRejectedBeforeWrites()
    {
        await Should.ThrowAsync<NachosValidationException>(() => Service.GetOrCreateWorkspaceAsync("W",
            configuration: new(CustomInstructions: "\uD800")));
        await Should.ThrowAsync<RequestValidationException>(() => Service.GetOrCreateSessionAsync("W", "S",
            configuration: new(Summary: new(MessagesPerShortSummary: 9))));
        var opaque = new JsonObject { ["payload"] = JsonValue.Create(new List<int> { 1, 2 }) };
        await Should.ThrowAsync<NachosValidationException>(() => Service.UpdatePeerAsync("W", "P", metadata: opaque));
        _store.Workspaces.ReceivedCalls().ShouldBeEmpty();
        _store.Sessions.ReceivedCalls().ShouldBeEmpty();
        _store.Peers.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task AlreadyCancelled_RequestHasNoEffects()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => Service.GetOrCreateWorkspaceAsync("W", ct: cts.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => Service.CreateKeyAsync("W", ct: cts.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => Service.AddGrantAsync("oid", null, GrantRoles.Admin, cts.Token));
        _store.Workspaces.ReceivedCalls().ShouldBeEmpty();
        _store.Grants.ReceivedCalls().ShouldBeEmpty();
        _issuer.ReceivedCalls().ShouldBeEmpty();
    }

    private static void CheckPage<T>(Page<T> page, string id, Func<T, string> getId)
    {
        getId(page.Items.ShouldHaveSingleItem()).ShouldBe(id);
        page.Total.ShouldBe(12);
        page.PageNumber.ShouldBe(2);
        page.Size.ShouldBe(3);
        page.Pages.ShouldBe(4);
    }
}
