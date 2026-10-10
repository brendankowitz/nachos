using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.SqlServer.Storage;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// Values longer than a bounded column are a validation error with a fixed message, checked before any write, instead
/// of SQL Server's truncation error (a 500 echoing the start of the value). Grants compare object ids and roles exactly.
/// </summary>
[Collection(SqlServerDockerGroup.Name)]
public sealed class SqlColumnLimitTests(SqlServerFixture fixture)
{
    private const string Secret = "SECRET";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static string Long(int length) => Secret + new string('o', length - Secret.Length);

    private async Task<(IMemoryStore Store, string Workspace)> StoreAsync()
    {
        var store = (await SqlTestDatabase.GetAsync(fixture, "column-limits")).CreateStore(TimeProvider.System);
        var workspace = $"ws-{Guid.NewGuid():N}";
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        await store.Sessions.GetOrCreateAsync(workspace, "s", null, null, null, Ct);
        return (store, workspace);
    }

    private static async Task ShouldRejectAsync(Func<Task> write, string message)
    {
        var rejected = await Should.ThrowAsync<NachosValidationException>(write);
        rejected.Detail.ShouldBe(message);
        rejected.Detail.ShouldNotContain(Secret);
    }

    [Fact]
    public async Task Names_OverTheColumnLength_AreRejected_AtTheLengthTheyAreStored()
    {
        var (store, workspace) = await StoreAsync();
        const string Message = "A workspace, peer or session name may hold at most 512 UTF-16 code units in the SQL Server provider.";
        var tooLong = Long(ColumnLimits.Name + 1);
        var longest = Long(ColumnLimits.Name);
        var peers = new Dictionary<string, SessionPeerConfig> { [tooLong] = new(null, null) };

        await ShouldRejectAsync(() => store.Workspaces.GetOrCreateAsync(tooLong, null, null, Ct), Message);
        await ShouldRejectAsync(() => store.Peers.GetOrCreateAsync(workspace, tooLong, null, null, Ct), Message);
        await ShouldRejectAsync(() => store.Sessions.GetOrCreateAsync(workspace, tooLong, null, null, null, Ct), Message);
        await ShouldRejectAsync(() => store.Sessions.GetOrCreateAsync(workspace, "s2", null, null, peers, Ct), Message);
        await ShouldRejectAsync(() => store.Sessions.AddPeersAsync(workspace, "s", peers, Ct), Message);
        await ShouldRejectAsync(() => store.Sessions.SetPeersAsync(workspace, "s", peers, Ct), Message);
        await ShouldRejectAsync(() => store.Messages.AppendAsync(workspace, "s", [new NewMessage(tooLong, "hi", 1, null, null)], null, Ct), Message);

        // The longest storable name round-trips everywhere.
        (await store.Workspaces.GetOrCreateAsync(longest, null, null, Ct)).Name.ShouldBe(longest);
        (await store.Peers.GetOrCreateAsync(workspace, longest, null, null, Ct)).Name.ShouldBe(longest);
        (await store.Sessions.GetOrCreateAsync(workspace, longest, null, null, null, Ct)).Name.ShouldBe(longest);
        (await store.Messages.AppendAsync(workspace, "s", [new NewMessage(longest, "hi", 1, null, null)], null, Ct)).Single().PeerName.ShouldBe(longest);
    }

    [Fact]
    public async Task IdempotencyKeyAndHash_OverTheColumnLength_AreRejected()
    {
        var (store, workspace) = await StoreAsync();
        IdempotencyWrite Write(string key, string hash) => new(key, hash, 201, _ => "{}", TimeSpan.FromMinutes(5));
        var hash = new string('a', ColumnLimits.RequestHash);

        await ShouldRejectAsync(
            () => store.Messages.AppendAsync(workspace, "s", [new NewMessage("p", "hi", 1, null, null)], Write(Long(ColumnLimits.IdempotencyKey + 1), hash), Ct),
            "An idempotency key may hold at most 255 UTF-16 code units in the SQL Server provider.");
        await ShouldRejectAsync(
            () => store.Messages.AppendAsync(workspace, "s", [new NewMessage("p", "hi", 1, null, null)], Write("key", Long(ColumnLimits.RequestHash + 1)), Ct),
            "An idempotency request hash may hold at most 64 characters in the SQL Server provider.");

        var key = Long(ColumnLimits.IdempotencyKey);
        await store.Messages.AppendAsync(workspace, "s", [new NewMessage("p", "hi", 1, null, null)], Write(key, hash), Ct);
        (await store.Idempotency.TryGetAsync(workspace, key, Ct)).ShouldNotBeNull().RequestHash.ShouldBe(hash);
    }

    [Fact]
    public async Task GrantObjectIdAndRole_OverTheColumnLength_AreRejected()
    {
        var (store, workspace) = await StoreAsync();

        await ShouldRejectAsync(
            () => store.Grants.AddAsync(new GrantRecord(Long(ColumnLimits.ObjectId + 1), workspace, GrantRoles.Workspace), Ct),
            "A grant's object id may hold at most 64 UTF-16 code units in the SQL Server provider.");
        await ShouldRejectAsync(
            () => store.Grants.AddAsync(new GrantRecord("object", workspace, Long(ColumnLimits.Role + 1)), Ct),
            "A grant's role may hold at most 32 UTF-16 code units in the SQL Server provider.");

        var objectId = Long(ColumnLimits.ObjectId);
        await store.Grants.AddAsync(new GrantRecord(objectId, workspace, GrantRoles.Workspace), Ct);
        (await store.Grants.ListAsync(objectId, Ct)).ShouldBe([new GrantRecord(objectId, workspace, GrantRoles.Workspace)]);
    }

    [Fact]
    public async Task GrantRoles_AreCaseExact_AndADuplicateIsANoOp()
    {
        var (store, _) = await StoreAsync();
        var objectId = $"obj-{Guid.NewGuid():N}";

        await store.Grants.AddAsync(new GrantRecord(objectId, null, "Nachos.Admin"), Ct);
        await store.Grants.AddAsync(new GrantRecord(objectId, null, "nachos.admin"), Ct);
        await store.Grants.AddAsync(new GrantRecord(objectId, null, "Nachos.Admin"), Ct);

        (await store.Grants.ListAsync(objectId, Ct)).Select(g => g.Role).ShouldBe(["Nachos.Admin", "nachos.admin"], ignoreOrder: true);
    }

    [Fact]
    public async Task GrantDifferingOnlyByTrailingSpaces_IsRejected_NotSilentlyDropped()
    {
        var (store, _) = await StoreAsync();
        var objectId = $"obj-{Guid.NewGuid():N}";
        await store.Grants.AddAsync(new GrantRecord(objectId, null, GrantRoles.Admin), Ct);

        await ShouldRejectAsync(
            () => store.Grants.AddAsync(new GrantRecord(objectId + " ", null, GrantRoles.Admin), Ct),
            "The grant differs from an existing grant only by trailing spaces in its object id or role, which the SQL Server provider cannot store side by side.");
        await ShouldRejectAsync(
            () => store.Grants.AddAsync(new GrantRecord(objectId, null, GrantRoles.Admin + " "), Ct),
            "The grant differs from an existing grant only by trailing spaces in its object id or role, which the SQL Server provider cannot store side by side.");
        (await store.Grants.ListAsync(objectId, Ct)).ShouldBe([new GrantRecord(objectId, null, GrantRoles.Admin)]);
    }
}
