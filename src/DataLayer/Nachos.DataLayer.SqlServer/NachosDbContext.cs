using Microsoft.EntityFrameworkCore;
using Nachos.DataLayer.SqlServer.Entities;

namespace Nachos.DataLayer.SqlServer;

/// <summary>
/// Maps the dacpac-owned tables for reads and paging. It never creates or migrates anything: DDL lives only in the
/// dacpac (see <c>SchemaDeployer</c>), and writes are explicit SQL whose locking the stores document.
/// </summary>
/// <remarks>
/// JSON columns are mapped as their stored text, so the stores alone decide how JSON is read (see
/// <c>Storage.SqlJson</c>). Not thread-safe: the stores create one context per operation.
/// </remarks>
internal sealed class NachosDbContext(DbContextOptions<NachosDbContext> options) : DbContext(options)
{
    public DbSet<WorkspaceEntity> Workspaces => Set<WorkspaceEntity>();

    public DbSet<PeerEntity> Peers => Set<PeerEntity>();

    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();

    public DbSet<SessionPeerEntity> SessionPeers => Set<SessionPeerEntity>();

    public DbSet<MessageEntity> Messages => Set<MessageEntity>();

    public DbSet<IdempotencyRecordEntity> IdempotencyRecords => Set<IdempotencyRecordEntity>();

    public DbSet<PrincipalGrantEntity> PrincipalGrants => Set<PrincipalGrantEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkspaceEntity>(workspace =>
        {
            workspace.ToTable("Workspaces", "dbo");
            workspace.Property(w => w.Name).HasMaxLength(512);
            Json(workspace.Property(w => w.Metadata));
            Json(workspace.Property(w => w.Configuration));
        });

        modelBuilder.Entity<PeerEntity>(peer =>
        {
            peer.ToTable("Peers", "dbo");
            peer.Property(p => p.Name).HasMaxLength(512);
            Json(peer.Property(p => p.Metadata));
            Json(peer.Property(p => p.Configuration));
        });

        modelBuilder.Entity<SessionEntity>(session =>
        {
            session.ToTable("Sessions", "dbo");
            session.Property(s => s.Name).HasMaxLength(512);
            Json(session.Property(s => s.Metadata));
            Json(session.Property(s => s.Configuration));
        });

        modelBuilder.Entity<SessionPeerEntity>(member =>
        {
            member.ToTable("SessionPeers", "dbo");
            member.HasKey(m => new { m.WorkspaceId, m.SessionId, m.PeerId });
            Json(member.Property(m => m.Configuration));
        });

        modelBuilder.Entity<MessageEntity>(message =>
        {
            message.ToTable("Messages", "dbo");
            message.Property(m => m.PublicId).HasMaxLength(32);
            Json(message.Property(m => m.Metadata));
        });

        modelBuilder.Entity<IdempotencyRecordEntity>(record =>
        {
            record.ToTable("IdempotencyRecords", "dbo");
            record.Property(r => r.KeyHash).HasColumnType("binary(32)");
            record.Property(r => r.Key).HasMaxLength(255);
            record.Property(r => r.RequestHash).HasColumnType("char(64)");
        });

        modelBuilder.Entity<PrincipalGrantEntity>(grant =>
        {
            grant.ToTable("PrincipalGrants", "dbo");
            grant.Property(g => g.ObjectId).HasMaxLength(64);
            grant.Property(g => g.Role).HasMaxLength(32);
        });
    }

    /// <summary>JSON is stored as text in <c>nvarchar(max)</c>, which a CHECK in the dacpac requires to be a JSON object.</summary>
    private static void Json(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<string> property) =>
        property.HasColumnType("nvarchar(max)");
}
