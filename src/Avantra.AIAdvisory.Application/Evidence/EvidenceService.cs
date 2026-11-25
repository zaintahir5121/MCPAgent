using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Domain.Advisory;
using Avantra.AIAdvisory.Domain.Catalogue;
using Avantra.AIAdvisory.Domain.Knowledge;

namespace Avantra.AIAdvisory.Application.Evidence;

/// <summary>Collects, de-duplicates and numbers citations for one advisory result.</summary>
public sealed class CitationRegistry
{
    private readonly Dictionary<string, Citation> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Citation> _ordered = [];

    public IReadOnlyList<Citation> All => _ordered;

    public string Add(SourceReference s, string? snippet = null, bool demo = false) =>
        Add(s.DocumentId + "|" + s.Section, s.Title, s.Url, s.Section, snippet, s.Trust, demo);

    public string Add(KnowledgeHit h) =>
        Add(h.DocumentId + "|" + h.Section, h.DocumentTitle, h.SourceUrl, h.Section, h.Snippet, h.Trust, h.IsDemonstrationData);

    private string Add(string key, string title, string? url, string? section, string? snippet, SourceTrustLevel trust, bool demo)
    {
        if (_byKey.TryGetValue(key, out var existing))
        {
            if (existing.Snippet is null && snippet is not null)
            {
                var updated = existing with { Snippet = snippet, IsDemonstrationData = existing.IsDemonstrationData || demo };
                _byKey[key] = updated;
                _ordered[_ordered.IndexOf(existing)] = updated;
            }
            return existing.Id;
        }
        var c = new Citation($"C{_ordered.Count + 1}", title, url, section, snippet, trust, demo);
        _byKey[key] = c;
        _ordered.Add(c);
        return c.Id;
    }
}

/// <summary>
/// Attaches evidence to every coverage claim and grades it. A claim is only "Verified" when the catalogue entry is
/// curator-verified AND backed by a vendor-official source. Retrieval adds supporting passages (and their
/// citations) but never creates a claim on its own - the catalogue is the source of truth for "what a product does".
/// </summary>
public sealed class EvidenceService(IKnowledgeIndex index)
{
    public const double MinimumHitScore = 0.05;

    public async Task<SolutionOption> AttachEvidenceAsync(SolutionOption option, VendorPack pack, CitationRegistry registry, CancellationToken ct)
    {
        var updated = new List<RequirementCoverage>();
        foreach (var cov in option.Coverage)
        {
            if (cov.ProductCode is null) { updated.Add(cov); continue; }
            var product = pack.Products.First(p => p.Code == cov.ProductCode);
            var pc = product.GetCapability(cov.Requirement.CapabilityCode);
            var capability = pack.Capabilities.FirstOrDefault(c => c.Code == cov.Requirement.CapabilityCode);
            var ids = new List<string>();

            foreach (var src in pc?.Evidence ?? [])
                ids.Add(registry.Add(src, demo: pack.Vendor.IsDemonstrationData));

            var hits = await index.SearchAsync(new KnowledgeQuery(
                $"{product.Name} {capability?.Name} {string.Join(' ', capability?.Synonyms.Take(3) ?? [])}",
                pack.Vendor.Code, Top: 2, ProductCodes: [product.Code]), ct);
            var supporting = hits.Where(h => h.Score >= MinimumHitScore).Take(1).ToList();
            ids.AddRange(supporting.Select(registry.Add));

            var grade = Grade(product, pc, supporting);
            updated.Add(cov with { Evidence = grade, CitationIds = ids.Distinct().ToList() });
        }
        return option with { Coverage = updated };
    }

    internal static EvidenceGrade Grade(Product product, ProductCapability? pc, IReadOnlyList<KnowledgeHit> hits)
    {
        if (pc is null) return EvidenceGrade.Unsupported;
        var hasOfficial = pc.Evidence.Any(e => e.Trust >= SourceTrustLevel.VendorOfficial);
        if (product.Verification == VerificationStatus.Verified && hasOfficial) return EvidenceGrade.Verified;
        if (hits.Count > 0) return EvidenceGrade.DocumentSupported;
        return pc.Evidence.Count > 0 ? EvidenceGrade.CatalogueUnverified : EvidenceGrade.Unsupported;
    }
}
