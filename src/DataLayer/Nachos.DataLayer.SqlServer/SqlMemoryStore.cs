using Nachos.Abstractions.Stores;
using Nachos.DataLayer.SqlServer.Schema;
using Nachos.DataLayer.SqlServer.Storage;
using Nachos.DataLayer.SqlServer.Stores;

namespace Nachos.DataLayer.SqlServer;

/// <summary>An <see cref="IMemoryStore"/> over the dacpac-managed SQL Server schema.</summary>
/// <remarks>
/// <para>
/// <b>Lifetime and threading.</b> Registered scoped by <c>UseSqlServer</c>, but safe for concurrent use: every operation
/// opens its own context and connection (pooled). Before its first database access, each operation awaits
/// <see cref="SchemaGate.EnsureAsync"/>, which verifies the schema once per process and remembers the success.
/// </para>
/// <para>
/// <b>Rules.</b> Times come from the <see cref="TimeProvider"/>, never the database clock. Names and public ids
/// compare ordinally and case-sensitively, including trailing spaces. Lists are in creation order with the identity
/// column as the tiebreak (messages in <c>Seq</c> order). Writes that race (get-or-create, membership, <c>Seq</c>,
/// idempotency keys) follow the lock order documented on <c>Storage.Upserts</c>; RCSI makes plain reads lock-free.
/// </para>
/// <para>
/// <b>JSON</b> is stored losslessly as text in <c>nvarchar(max)</c> columns that a CHECK constraint requires to hold a
/// JSON object. Caller JSON passes through <c>StrictJsonData.ToCanonical</c> at every entry point and its canonical text
/// is stored exactly as emitted: numbers keep their spelling (<c>1e2</c>, <c>1E400</c>, 40-digit integers), with no
/// normalization and no size cap. The one limit SQL Server itself imposes is that a key longer than 4000 UTF-16 code
/// units is rejected with <see cref="Abstractions.NachosValidationException"/>, because <c>OPENJSON</c> truncates
/// longer keys (see <c>Storage.SqlJson</c>). Every returned object is freshly parsed.
/// </para>
/// <para>
/// <b>Serializer re-entry.</b> While an <see cref="Abstractions.Domain.IdempotencyWrite.SerializeResponse"/> callback
/// runs inside an append's transaction, every entry point of this instance called from its execution context throws
/// <see cref="InvalidOperationException"/> before touching the database, and the append rolls back.
/// </para>
/// </remarks>
public sealed class SqlMemoryStore : IMemoryStore
{
    public SqlMemoryStore(SqlServerOptions options, SchemaGate schemaGate, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(schemaGate);
        ArgumentNullException.ThrowIfNull(clock);

        var runtime = new SqlStoreRuntime(options, schemaGate, clock);
        Workspaces = new SqlWorkspaceStore(runtime);
        Peers = new SqlPeerStore(runtime);
        Sessions = new SqlSessionStore(runtime);
        Messages = new SqlMessageStore(runtime);
        Grants = new SqlGrantStore(runtime);
        Idempotency = new SqlIdempotencyStore(runtime);
    }

    public IWorkspaceStore Workspaces { get; }

    public IPeerStore Peers { get; }

    public ISessionStore Sessions { get; }

    public IMessageStore Messages { get; }

    public IGrantStore Grants { get; }

    public IIdempotencyStore Idempotency { get; }
}
