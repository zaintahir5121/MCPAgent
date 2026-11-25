using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Domain.Governance;
using Avantra.AIAdvisory.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Avantra.AIAdvisory.Infrastructure.Persistence;

public sealed class EfKnowledgeStore(AdvisoryDbContext db) : IKnowledgeStore
{
    public Task<KnowledgeDocument?> FindByExternalIdAsync(string vendorCode, string externalId, CancellationToken ct) =>
        db.Documents.Include(d => d.Chunks)
            .Where(d => d.VendorCode == vendorCode && d.ExternalId == externalId && d.Status != DocumentStatus.Superseded)
            .OrderByDescending(d => d.Version).FirstOrDefaultAsync(ct);

    public Task<KnowledgeDocument?> GetAsync(Guid id, CancellationToken ct) =>
        db.Documents.Include(d => d.Chunks).FirstOrDefaultAsync(d => d.Id == id, ct);

    public async Task AddAsync(KnowledgeDocument document, CancellationToken ct)
    {
        db.Documents.Add(document);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(KnowledgeDocument document, CancellationToken ct)
    {
        if (db.Entry(document).State == EntityState.Detached) db.Documents.Update(document);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<KnowledgeDocument>> ListAsync(string? vendorCode, DocumentStatus? status, CancellationToken ct)
    {
        var q = db.Documents.AsQueryable();
        if (vendorCode is not null) q = q.Where(d => d.VendorCode == vendorCode);
        if (status is not null) q = q.Where(d => d.Status == status);
        return await q.OrderBy(d => d.VendorCode).ThenBy(d => d.Title).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<KnowledgeDocument>> ListPublishedWithChunksAsync(CancellationToken ct) =>
        await db.Documents.AsNoTracking().Include(d => d.Chunks).Where(d => d.Status == DocumentStatus.Published).ToListAsync(ct);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return false;
        db.Documents.Remove(doc);
        await db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>Tenant-scoped session store: a session is only ever loaded with the caller's tenant id.</summary>
public sealed class EfSessionStore(AdvisoryDbContext db) : ISessionStore
{
    public Task<AdvisorySession?> GetAsync(Guid id, string tenantId, CancellationToken ct) =>
        db.Sessions.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId, ct);

    public async Task SaveAsync(AdvisorySession session, CancellationToken ct)
    {
        if (db.Entry(session).State == EntityState.Detached)
        {
            var exists = await db.Sessions.AsNoTracking().AnyAsync(s => s.Id == session.Id, ct);
            if (exists) db.Sessions.Update(session); else db.Sessions.Add(session);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AdvisorySession>> ListAsync(string tenantId, string? ownerId, int take, CancellationToken ct)
    {
        var q = db.Sessions.AsNoTracking().Where(s => s.TenantId == tenantId);
        if (ownerId is not null) q = q.Where(s => s.OwnerId == ownerId);
        return await q.OrderByDescending(s => s.UpdatedAt).Take(Math.Clamp(take, 1, 100)).ToListAsync(ct);
    }
}

/// <summary>Append-only audit trail in SQL, mirrored to structured logs (Application Insights).</summary>
public sealed class EfAuditLog(AdvisoryDbContext db, ILogger<EfAuditLog> logger) : IAuditLog
{
    public async Task WriteAsync(AuditEntry entry, CancellationToken ct)
    {
        db.Audit.Add(entry);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("AUDIT {Action} tenant={Tenant} actor={Actor} channel={Channel} resource={Resource} {Detail}",
            entry.Action, entry.TenantId, entry.ActorId, entry.Channel, entry.ResourceId, entry.Detail);
    }
}
