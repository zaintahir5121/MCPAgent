using Avantra.AIAdvisory.Domain.Governance;
using Avantra.AIAdvisory.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Avantra.AIAdvisory.Infrastructure.Persistence;

/// <summary>
/// Operational store: knowledge documents/chunks (system of record for the index), advisory sessions and audit.
/// SQLite for local demo, Azure SQL / SQL Server in production (same model).
/// </summary>
public sealed class AdvisoryDbContext(DbContextOptions<AdvisoryDbContext> options) : DbContext(options)
{
    public DbSet<KnowledgeDocument> Documents => Set<KnowledgeDocument>();
    public DbSet<KnowledgeChunk> Chunks => Set<KnowledgeChunk>();
    public DbSet<AdvisorySession> Sessions => Set<AdvisorySession>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var floatsToBytes = new ValueConverter<float[]?, byte[]?>(
            v => v == null ? null : ToBytes(v),
            v => v == null ? null : ToFloats(v));

        b.Entity<KnowledgeDocument>(e =>
        {
            e.ToTable("KnowledgeDocuments");
            e.HasKey(x => x.Id);
            e.Property(x => x.ExternalId).HasMaxLength(200).IsRequired();
            e.Property(x => x.VendorCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.SourceUrl).HasMaxLength(2000);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Trust).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.VendorCode, x.ExternalId, x.Version }).IsUnique();
            e.HasIndex(x => new { x.VendorCode, x.Status });
            e.HasMany(x => x.Chunks).WithOne().HasForeignKey(c => c.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<KnowledgeChunk>(e =>
        {
            e.ToTable("KnowledgeChunks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Section).HasMaxLength(500);
            e.Property(x => x.Embedding).HasConversion(floatsToBytes);
            e.HasIndex(x => new { x.DocumentId, x.Ordinal });
        });

        b.Entity<AdvisorySession>(e =>
        {
            e.ToTable("AdvisorySessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).HasMaxLength(100).IsRequired();
            e.Property(x => x.OwnerId).HasMaxLength(200).IsRequired();
            e.Property(x => x.VendorCode).HasMaxLength(50);
            e.Property(x => x.Channel).HasMaxLength(30);
            e.Property(x => x.Review).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.TenantId, x.OwnerId });
            e.OwnsMany(x => x.Turns, t => t.ToJson());
        });

        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("AuditLog");
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(100);
            e.HasIndex(x => new { x.TenantId, x.At });
        });

        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            // SQLite cannot order/compare DateTimeOffset natively.
            foreach (var entity in b.Model.GetEntityTypes())
                foreach (var p in entity.GetProperties().Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
                    p.SetValueConverter(new DateTimeOffsetToBinaryConverter());
        }
    }

    private static byte[] ToBytes(float[] v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] ToFloats(byte[] v)
    {
        var floats = new float[v.Length / sizeof(float)];
        Buffer.BlockCopy(v, 0, floats, 0, v.Length);
        return floats;
    }
}
