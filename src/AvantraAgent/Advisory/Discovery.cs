using System.Text;
using System.Text.RegularExpressions;
using Avantra.AIAdvisory.Domain.Advisory;
using Avantra.AIAdvisory.Domain.Catalogue;

namespace AvantraAgent.Advisory;

public sealed record DiscoveryRequirement(string Capability, string Priority, string Origin, string? CoveredBy, string Coverage);
public sealed record DiscoveryRecommendation(IReadOnlyList<string> Products, int MustCovered, int MustTotal, int FitPercent, IReadOnlyList<string> Gaps, IReadOnlyList<string> AvantraRules);
public sealed record DiscoveryAlternative(IReadOnlyList<string> Products, int FitPercent, IReadOnlyList<string> Missing);
public sealed record DiscoveryQualification(string Status, int FitPercent, int CompletenessPercent, int? EstimatedUsers, IReadOnlyList<string> Reasons);
public sealed record ServiceOpportunity(string Title, string Why);
public sealed record DiscoverySource(string Id, string Title, string? Url);

/// <summary>
/// Live discovery picture of one customer conversation: what we know, what they need, what fits, how qualified the
/// opportunity is, which Avantra services it creates and what to ask next. Rebuilt after every message.
/// </summary>
public sealed record DiscoveryState(
    string KnowledgeBase,
    string KnowledgeBaseName,
    OrganizationProfile? Organization,
    IReadOnlyList<string> PainPoints,
    IReadOnlyList<DiscoveryRequirement> Requirements,
    DiscoveryRecommendation? Recommendation,
    IReadOnlyList<DiscoveryAlternative> Alternatives,
    DiscoveryQualification Qualification,
    IReadOnlyList<ServiceOpportunity> ServicesOpportunities,
    IReadOnlyList<string> OpenQuestions,
    IReadOnlyList<string> LicensingInputs,
    IReadOnlyList<DiscoverySource> Sources,
    string? Disclaimer,
    DateTimeOffset UpdatedAt);

public static partial class DiscoveryBuilder
{
    [GeneratedRegex(@"\b(struggl\w*|problems?|pain\w*|challeng\w*|lack\w*|no (?:visibility|insight|single|central|clear|way|real)|manual\w*|spreadsheets?|excel|slow\w*|difficult\w*|hard to|can'?t|cannot|unable|delay\w*|overrun\w*|over-?allocat\w*|silo\w*|duplicat\w*|inconsistent|frustrat\w*|too many|poor|missed|late|blind)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PainWords();

    [GeneratedRegex(@"(?<=[.!?;\n])\s+|\n")]
    private static partial Regex Sentences();

    public static IReadOnlyList<string> PainPoints(IEnumerable<string> userMessages) =>
        userMessages.SelectMany(m => Sentences().Split(m)).Select(s => s.Trim().TrimEnd('.'))
            .Where(s => s.Length is > 12 and < 300 && !s.EndsWith('?') && PainWords().IsMatch(s))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();

    public static DiscoveryState Build(string kbCode, string kbName, AdvisoryResult? r, VendorPack? pack, IReadOnlyList<string> userMessages)
    {
        var pains = PainPoints(userMessages);
        if (r is null || pack is null)
            return new DiscoveryState(kbCode, kbName, null, pains, [], null, [],
                new DiscoveryQualification("Discovery started", 0, 0, null, ["Describe the organisation, its goals and current challenges to start the assessment."]),
                [], [], [], [], null, DateTimeOffset.UtcNow);

        string Cap(string code) => pack.Capabilities.FirstOrDefault(c => c.Code == code)?.Name ?? code;
        string Prod(string? code) => code is null ? "-" : pack.Products.FirstOrDefault(p => p.Code == code)?.Name ?? code;
        var o = r.Profile.Organization;
        var top = r.Options.FirstOrDefault();

        var requirements = r.Profile.Requirements.Select(q =>
        {
            var cov = top?.Coverage.FirstOrDefault(c => c.Requirement.Id == q.Id);
            return new DiscoveryRequirement(Cap(q.CapabilityCode), q.Priority.ToString(), q.Origin.ToString(),
                cov?.ProductCode is null ? null : Prod(cov.ProductCode), cov?.Coverage?.ToString() ?? "Gap");
        }).ToList();

        var musts = r.Profile.Requirements.Where(q => q.Priority == RequirementPriority.Must).ToList();
        var mustCovered = top is null ? 0 : top.Coverage.Count(c => c.Requirement.Priority == RequirementPriority.Must && c.ProductCode is not null);
        var recommendation = top is null ? null : new DiscoveryRecommendation(top.ProductNames, mustCovered, musts.Count,
            (int)Math.Round(top.WeightedCoverage * 100), top.Gaps.Select(g => Cap(g.CapabilityCode)).ToList(), top.AppliedRules.Select(a => a.Description).ToList());
        var alternatives = r.Options.Skip(1).Select(x => new DiscoveryAlternative(x.ProductNames, (int)Math.Round(x.WeightedCoverage * 100),
            x.Gaps.Select(g => Cap(g.CapabilityCode)).ToList())).ToList();

        // Completeness of the facts a Planview consultant needs before a solution workshop.
        var facts = new (string Name, bool Known)[]
        {
            ("organisation size", o.Employees is not null),
            ("project managers", o.ProjectManagers is not null),
            ("portfolio managers", o.PortfolioManagers is not null),
            ("team members / resource managers", o.TeamMembers is not null || o.ResourceManagers is not null),
            ("delivery approach", o.Methodology is not null),
            ("current tools", o.ExistingSystems.Count > 0),
            ("business units", o.BusinessUnits is not null),
            ("hosting / data residency", o.DeploymentPreference is not null),
        };
        var completeness = (int)Math.Round(100.0 * facts.Count(f => f.Known) / facts.Length);
        var fit = recommendation?.FitPercent ?? 0;
        var mustRatio = musts.Count == 0 ? (top is null ? 0 : 1.0) : mustCovered / (double)musts.Count;
        var users = new[] { o.ProjectManagers, o.PortfolioManagers, o.ResourceManagers, o.TeamMembers, o.Executives }.Where(n => n is not null).Sum(n => n!.Value);

        var reasons = new List<string>();
        string status;
        if (r.Profile.Requirements.Count == 0) { status = "Discovery started"; reasons.Add("No requirements identified yet."); }
        else if (top is null || mustRatio < 0.6) { status = "Weak fit — specialist review"; reasons.Add("Key must-have needs are not covered by the catalogue."); }
        else if (completeness < 50) { status = "Needs more discovery"; reasons.Add($"Only {completeness}% of key facts known."); }
        else if (mustRatio >= 0.999) { status = "Qualified for solution workshop"; reasons.Add("All must-have needs covered and enough facts known."); }
        else { status = "Partial fit"; reasons.Add($"{musts.Count - mustCovered} must-have need(s) not covered."); }
        if (pains.Count > 0) reasons.Add($"{pains.Count} pain point(s) captured.");
        var missingFacts = facts.Where(f => !f.Known).Select(f => f.Name).ToList();
        if (missingFacts.Count > 0) reasons.Add("Still unknown: " + string.Join(", ", missingFacts.Take(4)) + ".");

        var licensing = r.Licensing.SelectMany(l => l.SizingInputs).Where(s => s.Count is not null)
            .DistinctBy(s => s.Persona).Select(s => $"{s.Persona}: {s.Count}").ToList();

        return new DiscoveryState(kbCode, kbName, o, pains, requirements, recommendation, alternatives,
            new DiscoveryQualification(status, fit, completeness, users > 0 ? users : null, reasons),
            Services(r, pack, top), r.Clarifications.Select(c => c.Question).Take(5).ToList(), licensing,
            r.Citations.Take(10).Select(c => new DiscoverySource(c.Id, c.DocumentTitle, c.Url)).ToList(), r.DataDisclaimer, DateTimeOffset.UtcNow);
    }

    /// <summary>Avantra services suggested by the discovery so far (rule-based; a consultant confirms).</summary>
    internal static IReadOnlyList<ServiceOpportunity> Services(AdvisoryResult r, VendorPack pack, SolutionOption? top)
    {
        var o = r.Profile.Organization;
        var caps = r.Profile.Requirements.Select(q => q.CapabilityCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<ServiceOpportunity>();
        if (top is not null)
            list.Add(new("Solution design workshop", $"Confirm scope and design for {string.Join(" + ", top.ProductNames)}."));
        if (top is { ProductNames.Count: > 1 })
            list.Add(new("Multi-product architecture & integration", $"Connect {string.Join(", ", top.ProductNames)} into one process and reporting layer."));
        foreach (var sys in o.ExistingSystems)
        {
            if (sys is "Jira" or "Azure DevOps" or "ServiceNow" or "GitHub")
                list.Add(new($"{sys} integration", $"Keep {sys} work in sync with portfolio planning (e.g. via Planview Hub)."));
            else if (sys is "Microsoft Project" or "MS Project" or "Excel" or "Smartsheet")
                list.Add(new($"Migration from {sys}", $"Move existing plans and data from {sys} into Planview."));
            else if (sys is "SAP" or "Oracle" or "Workday" or "Dynamics 365")
                list.Add(new($"{sys} integration", "Bring actuals, cost and people data from the ERP/HR system."));
            else if (sys is "Power BI" or "Tableau")
                list.Add(new($"{sys} reporting", "Executive dashboards across Planview and other systems."));
        }
        if (caps.Contains("financial-management"))
            list.Add(new("Financial planning setup", "Configure budgets, forecasts and actuals, including the finance integration."));
        if (caps.Contains("resource-management") || caps.Contains("capacity-planning"))
            list.Add(new("Resource & capacity model setup", "Clean up roles, skills and availability data so capacity planning works."));
        if (caps.Contains("agile-planning") || o.Methodology is "agile" or "hybrid")
            list.Add(new("Agile-at-scale enablement", "Team board design and connecting agile delivery to the portfolio."));
        if ((o.Employees ?? 0) >= 1000 || (o.BusinessUnits ?? 0) >= 3)
            list.Add(new("Phased rollout & change management", "Enterprise rollout across business units with PMO process design and training."));
        if (top is not null && top.Gaps.Count > 0)
            list.Add(new("Gap assessment", "Decide how to handle: " + string.Join(", ", top.Gaps.Select(g => pack.Capabilities.FirstOrDefault(c => c.Code == g.CapabilityCode)?.Name ?? g.CapabilityCode)) + "."));
        return list.DistinctBy(s => s.Title).ToList();
    }

    /// <summary>Shareable discovery summary (Markdown) for email, CRM notes or the solution workshop.</summary>
    public static string ToMarkdown(DiscoveryState d)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Discovery summary — {d.KnowledgeBaseName}").AppendLine();
        sb.AppendLine($"_Generated {d.UpdatedAt:yyyy-MM-dd HH:mm} UTC by AvantraAgent. Draft for consultant review._").AppendLine();
        sb.AppendLine($"**Qualification:** {d.Qualification.Status} · fit {d.Qualification.FitPercent}% · discovery completeness {d.Qualification.CompletenessPercent}%"
                      + (d.Qualification.EstimatedUsers is { } u ? $" · ~{u} named users" : ""));
        foreach (var reason in d.Qualification.Reasons) sb.AppendLine($"- {reason}");
        sb.AppendLine();
        if (d.Organization is { } o)
        {
            sb.AppendLine("## Customer profile");
            void F(string k, object? v) { if (v is not null && v.ToString() != "") sb.AppendLine($"- {k}: {v}"); }
            F("Employees", o.Employees); F("Project managers", o.ProjectManagers); F("Portfolio managers", o.PortfolioManagers);
            F("Resource managers", o.ResourceManagers); F("Team members", o.TeamMembers); F("Business units", o.BusinessUnits);
            F("Delivery approach", o.Methodology); F("Hosting", o.DeploymentPreference);
            F("Current tools", o.ExistingSystems.Count > 0 ? string.Join(", ", o.ExistingSystems) : null);
            sb.AppendLine();
        }
        if (d.PainPoints.Count > 0) { sb.AppendLine("## Pain points"); foreach (var p in d.PainPoints) sb.AppendLine($"- {p}"); sb.AppendLine(); }
        if (d.Requirements.Count > 0)
        {
            sb.AppendLine("## Requirements").AppendLine("| Need | Priority | Covered by | Coverage |").AppendLine("|---|---|---|---|");
            foreach (var q in d.Requirements) sb.AppendLine($"| {q.Capability}{(q.Origin == "Inferred" ? " (inferred)" : "")} | {q.Priority} | {q.CoveredBy ?? "-"} | {q.Coverage} |");
            sb.AppendLine();
        }
        if (d.Recommendation is { } rec)
        {
            sb.AppendLine("## Recommended solution");
            sb.AppendLine($"**{string.Join(" + ", rec.Products)}** — {(rec.MustTotal > 0 ? $"covers {rec.MustCovered} of {rec.MustTotal} must-haves, " : "")}overall fit {rec.FitPercent}%.");
            if (rec.Gaps.Count > 0) sb.AppendLine($"Gaps: {string.Join(", ", rec.Gaps)}.");
            foreach (var a in d.Alternatives) sb.AppendLine($"- Alternative: {string.Join(" + ", a.Products)} — fit {a.FitPercent}%{(a.Missing.Count > 0 ? $", missing {string.Join(", ", a.Missing)}" : "")}");
            sb.AppendLine();
        }
        if (d.LicensingInputs.Count > 0)
        {
            sb.AppendLine("## Licensing — quote inputs (pricing confirmed with the vendor)");
            foreach (var l in d.LicensingInputs) sb.AppendLine($"- {l}");
            sb.AppendLine();
        }
        if (d.ServicesOpportunities.Count > 0) { sb.AppendLine("## Avantra services opportunities"); foreach (var s in d.ServicesOpportunities) sb.AppendLine($"- **{s.Title}** — {s.Why}"); sb.AppendLine(); }
        if (d.OpenQuestions.Count > 0) { sb.AppendLine("## Next discovery questions"); foreach (var (q, i) in d.OpenQuestions.Select((q, i) => (q, i))) sb.AppendLine($"{i + 1}. {q}"); sb.AppendLine(); }
        if (d.Sources.Count > 0) { sb.AppendLine("## Sources"); foreach (var s in d.Sources) sb.AppendLine($"- [{s.Id}] {s.Title}{(s.Url is null ? "" : $" — {s.Url}")}"); sb.AppendLine(); }
        if (d.Disclaimer is not null) sb.AppendLine($"_{d.Disclaimer}_");
        return sb.ToString();
    }
}
