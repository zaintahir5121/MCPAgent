using Avantra.AIAdvisory.Domain.Advisory;
using Avantra.AIAdvisory.Domain.Catalogue;
using Avantra.AIAdvisory.Domain.Governance;
using Avantra.AIAdvisory.Domain.Knowledge;

namespace Avantra.AIAdvisory.Application.Abstractions;

/// <summary>Read access to versioned vendor packs (catalogue, licensing, rules).</summary>
public interface ICatalogueRepository
{
    IReadOnlyList<Vendor> GetVendors();
    VendorPack? GetPack(string vendorCode);
}

public sealed record KnowledgeQuery(
    string Text,
    string VendorCode,
    int Top = 5,
    IReadOnlyList<string>? ProductCodes = null,
    SourceTrustLevel MinimumTrust = SourceTrustLevel.Unverified);

/// <summary>Search index port. Implementations: in-process hybrid (BM25 + vector) and Azure AI Search.</summary>
public interface IKnowledgeIndex
{
    Task IndexAsync(KnowledgeDocument document, CancellationToken ct);
    Task RemoveAsync(Guid documentId, CancellationToken ct);
    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(KnowledgeQuery query, CancellationToken ct);
}

/// <summary>System of record for knowledge documents (SQL).</summary>
public interface IKnowledgeStore
{
    Task<KnowledgeDocument?> FindByExternalIdAsync(string vendorCode, string externalId, CancellationToken ct);
    Task<KnowledgeDocument?> GetAsync(Guid id, CancellationToken ct);
    Task AddAsync(KnowledgeDocument document, CancellationToken ct);
    Task UpdateAsync(KnowledgeDocument document, CancellationToken ct);
    Task<IReadOnlyList<KnowledgeDocument>> ListAsync(string? vendorCode, DocumentStatus? status, CancellationToken ct);
    Task<IReadOnlyList<KnowledgeDocument>> ListPublishedWithChunksAsync(CancellationToken ct);
}

/// <summary>Turns free text into a structured requirement profile.</summary>
public interface IRequirementsExtractor
{
    string Name { get; }
    Task<RequirementProfile?> ExtractAsync(string text, VendorPack pack, CancellationToken ct);
}

/// <summary>Produces a grounded narrative from a fact sheet. Returning null means "use the deterministic template".</summary>
public interface INarrativeGenerator
{
    Task<string?> GenerateExecutiveSummaryAsync(string factSheetJson, CancellationToken ct);
}

public interface ISessionStore
{
    Task<AdvisorySession?> GetAsync(Guid id, string tenantId, CancellationToken ct);
    Task SaveAsync(AdvisorySession session, CancellationToken ct);
    Task<IReadOnlyList<AdvisorySession>> ListAsync(string tenantId, string? ownerId, int take, CancellationToken ct);
}

public interface IAuditLog
{
    Task WriteAsync(AuditEntry entry, CancellationToken ct);
}

/// <summary>Who is calling, from which tenant, through which channel (web, api, mcp/nexa).</summary>
public interface IUserContext
{
    string TenantId { get; }
    string UserId { get; }
    string Channel { get; }
    bool IsInRole(string role);
}

/// <summary>Status of the configured AI provider, for transparency in responses and health checks.</summary>
public interface IAiProviderInfo
{
    bool ChatEnabled { get; }
    bool EmbeddingsEnabled { get; }
    string? ChatModel { get; }
}

public static class Roles
{
    public const string AdvisoryUser = "Advisory.User";
    public const string AdvisoryReviewer = "Advisory.Reviewer";
    public const string KnowledgeAdmin = "Knowledge.Admin";
}
