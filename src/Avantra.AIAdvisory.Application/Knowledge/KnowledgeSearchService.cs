using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Domain.Catalogue;
using Avantra.AIAdvisory.Domain.Knowledge;

namespace Avantra.AIAdvisory.Application.Knowledge;

public sealed record KnowledgeSearchResult(string Query, string VendorCode, IReadOnlyList<KnowledgeHit> Hits, string Notice);

/// <summary>Vendor-isolated knowledge search used by API, MCP and the advisory pipeline.</summary>
public sealed class KnowledgeSearchService(IKnowledgeIndex index, ICatalogueRepository catalogue)
{
    public async Task<KnowledgeSearchResult> SearchAsync(string vendorCode, string query, int top, string? productCode, SourceTrustLevel minTrust, CancellationToken ct)
    {
        var pack = catalogue.GetPack(vendorCode) ?? throw new UnknownVendorException(vendorCode);
        top = Math.Clamp(top, 1, 20);
        var hits = await index.SearchAsync(new KnowledgeQuery(query, pack.Vendor.Code, top,
            productCode is null ? null : [productCode], minTrust), ct);
        var notice = hits.Any(h => h.IsDemonstrationData)
            ? pack.Vendor.DataDisclaimer
            : hits.Count == 0 ? "No published knowledge matched the query. Do not answer from general knowledge." : "Results are from published knowledge sources; cite them.";
        return new KnowledgeSearchResult(query, pack.Vendor.Code, hits, notice);
    }
}

public sealed class UnknownVendorException(string vendorCode) : Exception($"Unknown vendor domain '{vendorCode}'.")
{
    public string VendorCode { get; } = vendorCode;
}
