using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Domain.Advisory;
using Avantra.AIAdvisory.Domain.Catalogue;
using Microsoft.Extensions.Logging;

namespace Avantra.AIAdvisory.Application.Requirements;

/// <summary>
/// Runs the AI extractor (if any) and the deterministic extractor, then merges and validates the result.
/// The LLM may only map to capability codes that exist in the vendor pack: anything else is discarded.
/// Numbers found deterministically win over model-produced numbers (models mis-read figures).
/// </summary>
public sealed class RequirementsAnalyzer(
    IEnumerable<IRequirementsExtractor> extractors,
    ILogger<RequirementsAnalyzer> logger)
{
    public async Task<RequirementProfile> AnalyzeAsync(string text, VendorPack pack, CancellationToken ct)
    {
        var heuristic = extractors.OfType<HeuristicRequirementsExtractor>().FirstOrDefault() ?? new HeuristicRequirementsExtractor();
        var baseline = (await heuristic.ExtractAsync(text, pack, ct))!;

        RequirementProfile? ai = null;
        string? aiName = null;
        foreach (var extractor in extractors.Where(e => e is not HeuristicRequirementsExtractor))
        {
            try
            {
                ai = await extractor.ExtractAsync(text, pack, ct);
                aiName = extractor.Name;
                if (ai is not null) break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Requirements extractor {Extractor} failed; falling back to heuristic.", extractor.Name);
            }
        }

        if (ai is null) return baseline;
        return Merge(baseline, Validate(ai, pack), $"{aiName}+{heuristic.Name}");
    }

    internal static RequirementProfile Validate(RequirementProfile ai, VendorPack pack)
    {
        var valid = new HashSet<string>(pack.Capabilities.Select(c => c.Code), StringComparer.OrdinalIgnoreCase);
        var reqs = ai.Requirements
            .Where(r => valid.Contains(r.CapabilityCode))
            .GroupBy(r => r.CapabilityCode.ToLowerInvariant())
            .Select(g => g.OrderByDescending(r => r.Priority).First() with { CapabilityCode = g.Key })
            .ToList();
        var dropped = ai.Requirements.Where(r => !valid.Contains(r.CapabilityCode)).Select(r => r.Statement);
        return ai with { Requirements = reqs, UnmappedStatements = ai.UnmappedStatements.Concat(dropped).ToList() };
    }

    internal static RequirementProfile Merge(RequirementProfile baseline, RequirementProfile ai, string analyzerName)
    {
        var o = baseline.Organization;
        var a = ai.Organization;
        var org = new OrganizationProfile
        {
            OrganizationName = a.OrganizationName ?? o.OrganizationName,
            Industry = a.Industry ?? o.Industry,
            Employees = o.Employees ?? a.Employees,
            ProjectManagers = o.ProjectManagers ?? a.ProjectManagers,
            PortfolioManagers = o.PortfolioManagers ?? a.PortfolioManagers,
            ResourceManagers = o.ResourceManagers ?? a.ResourceManagers,
            TeamMembers = o.TeamMembers ?? a.TeamMembers,
            Executives = o.Executives ?? a.Executives,
            BusinessUnits = o.BusinessUnits ?? a.BusinessUnits,
            Methodology = a.Methodology ?? o.Methodology,
            DeploymentPreference = a.DeploymentPreference ?? o.DeploymentPreference,
            ExistingSystems = o.ExistingSystems.Union(a.ExistingSystems, StringComparer.OrdinalIgnoreCase).ToList(),
            Regions = o.Regions.Union(a.Regions, StringComparer.OrdinalIgnoreCase).ToList(),
        };

        var merged = new List<Requirement>();
        foreach (var r in ai.Requirements.Concat(baseline.Requirements))
        {
            var idx = merged.FindIndex(m => m.CapabilityCode == r.CapabilityCode);
            if (idx < 0) merged.Add(r);
            else if (merged[idx].Priority < r.Priority) merged[idx] = merged[idx] with { Priority = r.Priority };
            else if (merged[idx].Origin == RequirementOrigin.Inferred && r.Origin == RequirementOrigin.Stated)
                merged[idx] = merged[idx] with { Origin = RequirementOrigin.Stated };
        }
        merged = merged.Select((r, i) => r with { Id = $"R{i + 1}" }).ToList();

        var unmapped = ai.UnmappedStatements.Concat(baseline.UnmappedStatements).Distinct().Take(10).ToList();
        return new RequirementProfile(baseline.VendorCode, org, merged, unmapped, analyzerName);
    }
}
