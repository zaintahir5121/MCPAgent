using Avantra.AIAdvisory.Application.Requirements;
using Avantra.AIAdvisory.Domain.Catalogue;

namespace Avantra.AIAdvisory.KnowledgeBase.Ingestion;

/// <summary>
/// Deterministic classification of chunks against the vendor's product and capability model (names, aliases,
/// synonyms). Gives every chunk filterable metadata without an LLM; an LLM classifier can be layered on later.
/// </summary>
public static class KnowledgeClassifier
{
    public static (List<string> Products, List<string> Capabilities) Classify(string text, VendorPack pack, IEnumerable<string>? documentProducts = null)
    {
        var lower = text.ToLowerInvariant();
        var products = pack.Products
            .Where(p => p.Aliases.Append(p.Name).Any(a => a.Contains(' ')
                ? HeuristicRequirementsExtractor.ContainsTerm(lower, a.ToLowerInvariant())
                // Single-word aliases ("Portfolios", "Hub") are common words: match them as proper nouns only.
                : HeuristicRequirementsExtractor.ContainsTerm(text, a)))
            .Select(p => p.Code)
            .Union(documentProducts ?? [])
            .Distinct()
            .ToList();
        var caps = pack.Capabilities
            .Where(c => c.Synonyms.Append(c.Name).Any(s => HeuristicRequirementsExtractor.ContainsTerm(lower, s.ToLowerInvariant())))
            .Select(c => c.Code)
            .ToList();
        return (products, caps);
    }
}
