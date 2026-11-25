using Avantra.AIAdvisory.Domain.Advisory;
using Avantra.AIAdvisory.Domain.Catalogue;

namespace Avantra.AIAdvisory.Application.Recommendation;

/// <param name="MinSupersetGain">An alternative that only adds products to a better option must improve weighted coverage by at least this much.</param>
public sealed record RecommendationOptions(int MaxProductsPerOption = 3, int MaxOptions = 3, double PenaltyPerAdditionalProduct = 0.04, int CandidatePool = 8, double MinSupersetGain = 0.05);

/// <summary>
/// Deterministic, explainable solution-composition engine. No LLM involvement: given the same profile and the
/// same pack version it always returns the same ranking. It is a weighted set-cover over the capability model:
/// <list type="number">
/// <item>Score each product by how well it covers the requirements (Must=3, Should=2, Could=1; Core=1.0, Supported=0.7, ViaIntegration=0.4).</item>
/// <item>Enumerate bundles of 1..N products from the best candidates.</item>
/// <item>Rank lexicographically: Must-have coverage first, then weighted coverage minus complexity penalty plus Avantra fit rules.</item>
/// <item>Drop dominated bundles (adding a product that adds no coverage).</item>
/// </list>
/// </summary>
public sealed class RecommendationEngine
{
    public static double Weight(RequirementPriority p) => p switch
    {
        RequirementPriority.Must => 3,
        RequirementPriority.Should => 2,
        _ => 1,
    };

    public static double CoverageValue(CoverageLevel? c) => c switch
    {
        CoverageLevel.Core => 1.0,
        CoverageLevel.Supported => 0.7,
        CoverageLevel.ViaIntegration => 0.4,
        _ => 0,
    };

    public IReadOnlyList<SolutionOption> Recommend(RequirementProfile profile, VendorPack pack, RecommendationOptions? options = null)
    {
        options ??= new RecommendationOptions();
        var reqs = profile.Requirements;
        if (reqs.Count == 0) return [];

        var products = pack.Products.Where(p => p.Verification != VerificationStatus.Deprecated).ToList();
        var individual = products
            .Select(p => (Product: p, Score: reqs.Sum(r => Weight(r.Priority) * CoverageValue(p.GetCapability(r.CapabilityCode)?.Coverage))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Product.Code, StringComparer.Ordinal)
            .Take(options.CandidatePool)
            .Select(x => x.Product)
            .ToList();
        if (individual.Count == 0) return [];

        var evaluated = new List<Evaluated>();
        foreach (var bundle in Combinations(individual, Math.Min(options.MaxProductsPerOption, individual.Count)))
        {
            var e = EvaluateBundle(bundle, profile, pack, options);
            // Dominance: every product must contribute at least one "best" coverage.
            if (bundle.Count > 1 && bundle.Any(p => !e.Coverage.Any(c => c.Product == p))) continue;
            evaluated.Add(e);
        }

        var ranked = evaluated
            .OrderByDescending(e => Math.Round(e.MustCoverage, 4))
            .ThenByDescending(e => e.Score)
            .ThenBy(e => e.Bundle.Count)
            .ThenBy(e => string.Join(",", e.Bundle.Select(p => p.Code)), StringComparer.Ordinal)
            .ToList();

        // Diversity: do not return options that are pure supersets of a better-ranked option with no coverage gain.
        var chosen = new List<Evaluated>();
        foreach (var e in ranked)
        {
            if (chosen.Any(c => c.Bundle.All(e.Bundle.Contains) && e.Weighted < c.Weighted + options.MinSupersetGain)) continue;
            chosen.Add(e);
            if (chosen.Count == options.MaxOptions) break;
        }

        return chosen.Select((e, i) => ToOption(e, i + 1, reqs, pack)).ToList();
    }

    /// <summary>
    /// Evaluates a specific, caller-chosen product set (e.g. what the customer already owns) against the requirements.
    /// Used for gap analysis. Unknown product codes are ignored.
    /// </summary>
    public SolutionOption EvaluateProducts(RequirementProfile profile, VendorPack pack, IReadOnlyList<string> productCodes, RecommendationOptions? options = null)
    {
        var bundle = pack.Products.Where(p => productCodes.Contains(p.Code, StringComparer.OrdinalIgnoreCase)).ToList();
        var e = EvaluateBundle(bundle, profile, pack, options ?? new RecommendationOptions());
        return ToOption(e, 1, profile.Requirements, pack) with { OptionId = "EVAL", Label = "Evaluated product set" };
    }

    private static Evaluated EvaluateBundle(IReadOnlyList<Product> bundle, RequirementProfile profile, VendorPack pack, RecommendationOptions options)
    {
        var reqs = profile.Requirements;
        var totalWeight = Math.Max(1e-9, reqs.Sum(r => Weight(r.Priority)));
        var mustCount = reqs.Count(r => r.Priority == RequirementPriority.Must);
        var coverage = reqs.Select(r => BestFor(r, bundle)).ToList();
        var weighted = reqs.Select((r, i) => Weight(r.Priority) * CoverageValue(coverage[i].Level)).Sum() / totalWeight;
        var mustCov = mustCount == 0 ? 1.0 : reqs.Where((r, i) => r.Priority == RequirementPriority.Must && coverage[i].Level is not null).Count() / (double)mustCount;
        var rules = ApplyRules(pack, profile, bundle);
        var score = weighted - options.PenaltyPerAdditionalProduct * Math.Max(0, bundle.Count - 1) + rules.Sum(r => r.Adjustment);
        return new Evaluated(bundle, coverage, weighted, mustCov, Math.Round(score, 4), rules);
    }

    private static SolutionOption ToOption(Evaluated e, int rank, IReadOnlyList<Requirement> reqs, VendorPack pack)
    {
        var coverage = reqs.Select((r, i) =>
        {
            var (product, level) = e.Coverage[i];
            var explanation = product is null
                ? $"No product in this option covers '{CapName(pack, r.CapabilityCode)}' according to the catalogue."
                : $"{product.Name} provides '{CapName(pack, r.CapabilityCode)}' as a {Describe(level)} capability{NoteFor(product, r.CapabilityCode)}.";
            return new RequirementCoverage(r, product?.Code, level, EvidenceGrade.Unsupported, [], explanation);
        }).ToList();

        var gaps = coverage.Where(c => c.ProductCode is null).Select(c => c.Requirement).ToList();
        var considerations = e.Bundle.SelectMany(p => p.ImplementationConsiderations.Select(c => $"{p.Name}: {c}")).ToList();
        if (e.Bundle.Count > 1)
            considerations.Add($"Multi-product solution ({e.Bundle.Count} products): plan integration, data model alignment and a single reporting layer early.");

        var label = rank == 1 ? "Recommended" : $"Alternative {rank - 1}";
        return new SolutionOption(
            $"OPT-{rank}", rank, label,
            e.Bundle.Select(p => p.Code).ToList(),
            e.Bundle.Select(p => p.Name).ToList(),
            e.Score, Math.Round(e.MustCoverage, 4), Math.Round(e.Weighted, 4),
            coverage, gaps, e.Rules, considerations);
    }

    private static (Product? Product, CoverageLevel? Level) BestFor(Requirement r, IReadOnlyList<Product> bundle)
    {
        Product? best = null;
        CoverageLevel? level = null;
        foreach (var p in bundle)
        {
            var c = p.GetCapability(r.CapabilityCode);
            if (c is null) continue;
            if (level is null || c.Coverage > level) { best = p; level = c.Coverage; }
        }
        return (best, level);
    }

    private static List<AppliedRule> ApplyRules(VendorPack pack, RequirementProfile profile, IReadOnlyList<Product> bundle)
    {
        var applied = new List<AppliedRule>();
        var o = profile.Organization;
        var caps = profile.Requirements.Select(r => r.CapabilityCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in pack.FitRules)
        {
            var w = rule.When;
            if (w.Methodology is not null && !string.Equals(w.Methodology, o.Methodology, StringComparison.OrdinalIgnoreCase)) continue;
            if (w.MinEmployees is not null && (o.Employees ?? 0) < w.MinEmployees) continue;
            if (w.MaxEmployees is not null && (o.Employees is null || o.Employees > w.MaxEmployees)) continue;
            if (w.CapabilityPresent is not null && !caps.Contains(w.CapabilityPresent)) continue;
            if (w.DeploymentPreference is not null && !string.Equals(w.DeploymentPreference, o.DeploymentPreference, StringComparison.OrdinalIgnoreCase)) continue;
            if (!rule.ProductCodes.All(pc => bundle.Any(b => b.Code == pc))) continue;
            applied.Add(new AppliedRule(rule.Code, rule.Description, rule.ScoreAdjustment, rule.Rationale, rule.RequiresBusinessConfirmation));
        }
        return applied;
    }

    private static IEnumerable<IReadOnlyList<Product>> Combinations(IReadOnlyList<Product> items, int maxSize)
    {
        for (var size = 1; size <= maxSize; size++)
            foreach (var c in Combine(items, size, 0, []))
                yield return c;
    }

    private static IEnumerable<IReadOnlyList<Product>> Combine(IReadOnlyList<Product> items, int size, int start, List<Product> current)
    {
        if (current.Count == size) { yield return current.ToList(); yield break; }
        for (var i = start; i < items.Count; i++)
        {
            current.Add(items[i]);
            foreach (var c in Combine(items, size, i + 1, current)) yield return c;
            current.RemoveAt(current.Count - 1);
        }
    }

    private static string CapName(VendorPack pack, string code) => pack.Capabilities.FirstOrDefault(c => c.Code == code)?.Name ?? code;

    private static string Describe(CoverageLevel? l) => l switch
    {
        CoverageLevel.Core => "core",
        CoverageLevel.Supported => "supported",
        CoverageLevel.ViaIntegration => "via-integration",
        _ => "unknown",
    };

    private static string NoteFor(Product p, string cap) =>
        p.GetCapability(cap)?.Notes is { Length: > 0 } n ? $" ({n})" : string.Empty;

    private sealed record Evaluated(
        IReadOnlyList<Product> Bundle,
        List<(Product? Product, CoverageLevel? Level)> Coverage,
        double Weighted,
        double MustCoverage,
        double Score,
        List<AppliedRule> Rules);
}
