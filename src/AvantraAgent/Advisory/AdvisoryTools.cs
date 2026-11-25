using System.Text.Json;
using System.Text.RegularExpressions;
using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Application.Advisory;
using Avantra.AIAdvisory.Application.Evidence;
using Avantra.AIAdvisory.Application.Knowledge;
using Avantra.AIAdvisory.Application.Licensing;
using Avantra.AIAdvisory.Domain.Advisory;
using Avantra.AIAdvisory.Domain.Catalogue;
using Avantra.AIAdvisory.Domain.Knowledge;

namespace AvantraAgent.Advisory;

public sealed record KnowledgeHitView(string KnowledgeBase, string KnowledgeBaseName, string DocumentTitle, string? Section, string Snippet, double Score, string? SourceUrl, bool IsDemonstrationData);

/// <summary>
/// Tools over every knowledge base. Search works for any knowledge base; product advisory (recommend, compare,
/// licensing) is available where a vendor pack (product catalogue + rules) exists for the knowledge base code.
/// </summary>
public sealed class AdvisoryTools(
    IKnowledgeBaseRegistry registry, ICatalogueRepository catalogue, KnowledgeSearchService search,
    AdvisoryOrchestrator orchestrator, ProductComparisonService comparison, LicensingEvaluator licensing)
{
    public const string All = "all";
    public static readonly JsonSerializerOptions Json = AdvisoryOrchestrator.Json;

    public IReadOnlyList<KnowledgeBaseDefinition> KnowledgeBases => registry.All.Where(k => k.IsEnabled).ToList();

    public bool HasAdvisory(string code) => catalogue.GetPack(code) is not null && registry.Get(code)?.IsEnabled == true;

    public IReadOnlyList<string> AdvisoryKnowledgeBases => KnowledgeBases.Where(k => HasAdvisory(k.Code)).Select(k => k.Code).ToList();

    public VendorPack Pack(string code) => catalogue.GetPack(code) ?? throw new ArgumentException($"Knowledge base '{code}' has no product catalogue, so product advisory is not available. Use search instead.");

    public string Normalize(string? kb)
    {
        if (string.IsNullOrWhiteSpace(kb) || kb.Equals(All, StringComparison.OrdinalIgnoreCase)) return All;
        var match = KnowledgeBases.FirstOrDefault(k => k.Code.Equals(kb.Trim(), StringComparison.OrdinalIgnoreCase) || k.Name.Equals(kb.Trim(), StringComparison.OrdinalIgnoreCase));
        return match?.Code ?? throw new KeyNotFoundException($"Knowledge base '{kb}' does not exist. Available: {string.Join(", ", KnowledgeBases.Select(k => k.Code))}.");
    }

    /// <summary>Search one knowledge base, or all of them (each has its own index; results are merged by score).</summary>
    public async Task<IReadOnlyList<KnowledgeHitView>> SearchAsync(string? knowledgeBase, string query, int top, CancellationToken ct)
    {
        var code = Normalize(knowledgeBase);
        top = Math.Clamp(top, 1, 20);
        var targets = code == All ? KnowledgeBases : [registry.Get(code)!];
        var hits = new List<KnowledgeHitView>();
        foreach (var kb in targets)
        {
            var r = await search.SearchAsync(kb.Code, query, top, null, SourceTrustLevel.Unverified, ct);
            hits.AddRange(r.Hits.Select(h => new KnowledgeHitView(kb.Code, kb.Name, h.DocumentTitle, h.Section, h.Snippet, h.Score, h.SourceUrl, h.IsDemonstrationData)));
        }
        return hits.OrderByDescending(h => h.Score).Take(top).ToList();
    }

    public Task<AdvisoryResult> RecommendAsync(string knowledgeBase, string requirements, Guid? sessionId, CancellationToken ct)
    {
        Pack(knowledgeBase);
        return orchestrator.AdviseAsync(new AdvisoryRequest(knowledgeBase, requirements, sessionId), ct);
    }

    public ProductComparison Compare(string knowledgeBase, IReadOnlyList<string> products)
    {
        Pack(knowledgeBase);
        return comparison.Compare(knowledgeBase, products);
    }

    public IReadOnlyList<LicensingAssessment> Licensing(string knowledgeBase, IReadOnlyList<string> products, OrganizationProfile organization)
    {
        var pack = Pack(knowledgeBase);
        var codes = products.SelectMany(p => FindProducts(knowledgeBase, p).DefaultIfEmpty(p)).Distinct().ToList();
        return licensing.Evaluate(codes, new RequirementProfile(pack.Vendor.Code, organization, [], [], "none"), pack, new CitationRegistry());
    }

    public IReadOnlyList<object> ListProducts(string knowledgeBase) => Pack(knowledgeBase).Products.Select(p => (object)new
    {
        p.Code, p.Name, p.Summary, verification = p.Verification.ToString(),
        capabilities = p.Capabilities.Select(c => $"{c.CapabilityCode} ({c.Coverage})"),
    }).ToList();

    /// <summary>Product codes of a knowledge base mentioned in free text (by code, name or alias).</summary>
    public IReadOnlyList<string> FindProducts(string knowledgeBase, string text)
    {
        if (catalogue.GetPack(knowledgeBase) is not { } pack) return [];
        return pack.Products.Where(p => p.Aliases.Append(p.Name).Append(p.Code).Any(a => a.Contains(' ') || a.Contains('-')
                ? text.Contains(a, StringComparison.OrdinalIgnoreCase)
                : Regex.IsMatch(text, $@"\b{Regex.Escape(a)}\b", RegexOptions.IgnoreCase)))
            .Select(p => p.Code).Distinct().ToList();
    }

    /// <summary>Which advisory knowledge base a message is about: the selected one, the one it names, or the only one.</summary>
    public string? ResolveAdvisoryKnowledgeBase(string selected, string message)
    {
        if (selected != All) return HasAdvisory(selected) ? selected : null;
        var named = AdvisoryKnowledgeBases.Where(code =>
        {
            var kb = registry.Get(code)!;
            return Regex.IsMatch(message, $@"\b({Regex.Escape(kb.Code)}|{Regex.Escape(kb.Name)})\b", RegexOptions.IgnoreCase) || FindProducts(code, message).Count > 0;
        }).ToList();
        if (named.Count == 1) return named[0];
        return AdvisoryKnowledgeBases.Count == 1 ? AdvisoryKnowledgeBases[0] : null;
    }

    public static string ToJson(object value) => JsonSerializer.Serialize(value, Json);
}
