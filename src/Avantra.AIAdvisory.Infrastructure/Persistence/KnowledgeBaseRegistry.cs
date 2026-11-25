using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Avantra.AIAdvisory.Infrastructure.Persistence;

/// <summary>
/// Database-backed knowledge base registry with an in-memory cache (reads are hot-path; writes are rare).
/// Singleton: uses a fresh DbContext scope per write.
/// </summary>
public sealed partial class EfKnowledgeBaseRegistry(IServiceScopeFactory scopes) : IKnowledgeBaseRegistry
{
    private readonly ConcurrentDictionary<string, KnowledgeBaseDefinition> _cache = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^[a-z][a-z0-9-]{1,48}[a-z0-9]$")]
    private static partial Regex CodeRule();

    public IReadOnlyList<KnowledgeBaseDefinition> All => _cache.Values.OrderBy(k => k.Name).ToList();

    public KnowledgeBaseDefinition? Get(string code) => _cache.GetValueOrDefault(code ?? string.Empty);

    public static bool IsValidCode(string code) => CodeRule().IsMatch(code ?? string.Empty);

    public async Task LoadAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AdvisoryDbContext>();
        _cache.Clear();
        foreach (var kb in await db.KnowledgeBases.AsNoTracking().ToListAsync(ct)) _cache[kb.Code] = kb;
    }

    public async Task<KnowledgeBaseDefinition> CreateAsync(KnowledgeBaseDefinition kb, CancellationToken ct)
    {
        kb.Code = (kb.Code ?? "").Trim().ToLowerInvariant();
        if (!IsValidCode(kb.Code)) throw new ArgumentException("Code must be 3-50 characters: lower-case letters, digits and hyphens, starting with a letter (e.g. 'salesforce').");
        if (string.IsNullOrWhiteSpace(kb.Name)) throw new ArgumentException("Name is required.");
        kb.IndexName = string.IsNullOrWhiteSpace(kb.IndexName) ? $"{kb.Code}-index" : kb.IndexName.Trim().ToLowerInvariant();
        if (!IsValidCode(kb.IndexName)) throw new ArgumentException("Index name must be lower-case letters, digits and hyphens.");
        if (kb.SyncIntervalHours < 0) throw new ArgumentException("SyncIntervalHours cannot be negative.");
        if (_cache.ContainsKey(kb.Code)) throw new InvalidOperationException($"Knowledge base '{kb.Code}' already exists.");
        if (_cache.Values.Any(x => x.IndexName == kb.IndexName)) throw new InvalidOperationException($"Index '{kb.IndexName}' is already used by another knowledge base.");

        kb.CreatedAt = kb.UpdatedAt = DateTimeOffset.UtcNow;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AdvisoryDbContext>();
        db.KnowledgeBases.Add(kb);
        await db.SaveChangesAsync(ct);
        _cache[kb.Code] = kb;
        return kb;
    }

    public async Task<KnowledgeBaseDefinition?> UpdateAsync(string code, Action<KnowledgeBaseDefinition> change, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AdvisoryDbContext>();
        var kb = await db.KnowledgeBases.FirstOrDefaultAsync(x => x.Code == code, ct);
        if (kb is null) return null;
        change(kb);
        if (kb.SyncIntervalHours < 0) throw new ArgumentException("SyncIntervalHours cannot be negative.");
        kb.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        _cache[kb.Code] = kb;
        return kb;
    }

    /// <summary>Removes the knowledge base and its documents from the database. The caller removes index content.</summary>
    public async Task<IReadOnlyList<Guid>> DeleteAsync(string code, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AdvisoryDbContext>();
        var docs = await db.Documents.Where(d => d.VendorCode == code).ToListAsync(ct);
        db.Documents.RemoveRange(docs);
        var kb = await db.KnowledgeBases.FirstOrDefaultAsync(x => x.Code == code, ct);
        if (kb is not null) db.KnowledgeBases.Remove(kb);
        await db.SaveChangesAsync(ct);
        _cache.TryRemove(code, out _);
        return docs.Select(d => d.Id).ToList();
    }
}
