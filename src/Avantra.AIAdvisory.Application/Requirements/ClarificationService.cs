using Avantra.AIAdvisory.Domain.Advisory;
using Avantra.AIAdvisory.Domain.Catalogue;

namespace Avantra.AIAdvisory.Application.Requirements;

/// <summary>Data-driven gap detection: each vendor pack declares which facts it needs and why.</summary>
public sealed class ClarificationService
{
    public IReadOnlyList<ClarificationQuestion> GetQuestions(RequirementProfile profile, VendorPack pack, int max = 6)
    {
        var o = profile.Organization;
        var caps = profile.Requirements.Select(r => r.CapabilityCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var questions = new List<ClarificationQuestion>();

        if (profile.Requirements.Count == 0)
            questions.Add(new ClarificationQuestion("no-requirements",
                "Which business outcomes or capabilities are you looking for (for example portfolio prioritisation, resource and capacity planning, financial planning, agile delivery, work collaboration)?",
                "No capabilities could be identified, so no product can be recommended yet.", 0));

        foreach (var rule in pack.ClarificationRules.OrderBy(r => r.Priority))
        {
            if (rule.OnlyIfCapability is not null && !caps.Contains(rule.OnlyIfCapability)) continue;
            if (IsKnown(rule.Field, o)) continue;
            questions.Add(new ClarificationQuestion(rule.Code, rule.Question, rule.WhyItMatters, rule.Priority));
        }

        return questions.OrderBy(q => q.Priority).Take(max).ToList();
    }

    internal static bool IsKnown(string field, OrganizationProfile o) => field switch
    {
        "employees" => o.Employees is not null,
        "projectManagers" => o.ProjectManagers is not null,
        "portfolioManagers" => o.PortfolioManagers is not null,
        "resourceManagers" => o.ResourceManagers is not null,
        "teamMembers" => o.TeamMembers is not null,
        "executives" => o.Executives is not null,
        "businessUnits" => o.BusinessUnits is not null,
        "methodology" => o.Methodology is not null,
        "deploymentPreference" => o.DeploymentPreference is not null,
        "existingSystems" => o.ExistingSystems.Count > 0,
        "industry" => o.Industry is not null,
        "regions" => o.Regions.Count > 0,
        _ => true,
    };
}
