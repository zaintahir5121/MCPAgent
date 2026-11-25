using System.Text;
using Avantra.AIAdvisory.Application.Advisory;
using Avantra.AIAdvisory.Domain.Advisory;

namespace Avantra.AIAdvisory.Application.Reporting;

/// <summary>Renders an <see cref="AdvisoryResult"/> as a shareable Markdown advisory report (HTML/PDF/DOCX renderers sit on top).</summary>
public sealed class AdvisoryReportGenerator
{
    public AdvisoryReport Generate(AdvisoryResult r, string vendorName, string? customerName = null)
    {
        var title = $"{vendorName} Solution Advisory Report{(customerName is null ? "" : $" - {customerName}")}";
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"*Session:* `{r.SessionId}` · *Generated:* {r.GeneratedAt:yyyy-MM-dd HH:mm} UTC · *Status:* **{StatusText(r.Review)}**");
        sb.AppendLine();
        if (r.Review != ReviewStatus.Approved)
            sb.AppendLine("> **DRAFT - not yet reviewed by an Avantra solution consultant. Not for external distribution.**").AppendLine();

        sb.AppendLine("## 1. Executive summary").AppendLine();
        sb.AppendLine(r.ExecutiveSummary ?? "-").AppendLine();
        sb.AppendLine($"<sub>Summary source: {r.ExecutiveSummarySource}</sub>").AppendLine();

        var o = r.Profile.Organization;
        sb.AppendLine("## 2. Customer context").AppendLine();
        sb.AppendLine("| Attribute | Value |").AppendLine("|---|---|");
        Row(sb, "Employees", o.Employees);
        Row(sb, "Project managers", o.ProjectManagers);
        Row(sb, "Portfolio managers", o.PortfolioManagers);
        Row(sb, "Resource managers", o.ResourceManagers);
        Row(sb, "Team members", o.TeamMembers);
        Row(sb, "Business units", o.BusinessUnits);
        Row(sb, "Delivery methodology", o.Methodology);
        Row(sb, "Deployment preference", o.DeploymentPreference);
        Row(sb, "Existing systems", o.ExistingSystems.Count == 0 ? null : string.Join(", ", o.ExistingSystems));
        sb.AppendLine();

        sb.AppendLine("## 3. Requirements").AppendLine();
        sb.AppendLine("| ID | Requirement | Capability | Priority | Origin |").AppendLine("|---|---|---|---|---|");
        foreach (var q in r.Profile.Requirements)
            sb.AppendLine($"| {q.Id} | {Esc(q.Statement)} | `{q.CapabilityCode}` | {q.Priority} | {q.Origin} |");
        sb.AppendLine().AppendLine($"<sub>Analyser: {r.Profile.AnalyzerUsed}</sub>").AppendLine();

        if (r.Options.Count > 0)
        {
            var top = r.Options[0];
            sb.AppendLine("## 4. Recommended solution").AppendLine();
            sb.AppendLine($"**{string.Join(" + ", top.ProductNames)}** - must-have coverage {Pct(top.MustHaveCoverage)}, weighted coverage {Pct(top.WeightedCoverage)}, score {top.Score:0.000}.").AppendLine();
            sb.AppendLine("| Req | Covered by | Coverage | Evidence | Sources |").AppendLine("|---|---|---|---|---|");
            foreach (var c in top.Coverage)
                sb.AppendLine($"| {c.Requirement.Id} | {Name(top, c.ProductCode)} | {c.Coverage?.ToString() ?? "**GAP**"} | {EvidenceText(c.Evidence)} | {string.Join(" ", c.CitationIds.Select(id => $"[{id}]"))} |");
            sb.AppendLine();
            if (top.AppliedRules.Count > 0)
            {
                sb.AppendLine("**Avantra advisory rules applied** (internal heuristics, not vendor facts):").AppendLine();
                foreach (var a in top.AppliedRules)
                    sb.AppendLine($"- {a.Description} ({a.Adjustment:+0.00;-0.00}) - {a.Rationale}{(a.RequiresBusinessConfirmation ? " *(awaiting business confirmation)*" : "")}");
                sb.AppendLine();
            }

            if (r.Options.Count > 1)
            {
                sb.AppendLine("## 5. Alternatives compared").AppendLine();
                sb.AppendLine("| Option | Products | Must-have | Weighted | Score | Gaps |").AppendLine("|---|---|---|---|---|---|");
                foreach (var opt in r.Options)
                    sb.AppendLine($"| {opt.Label} | {string.Join(" + ", opt.ProductNames)} | {Pct(opt.MustHaveCoverage)} | {Pct(opt.WeightedCoverage)} | {opt.Score:0.000} | {(opt.Gaps.Count == 0 ? "-" : string.Join(", ", opt.Gaps.Select(g => g.Id)))} |");
                sb.AppendLine();
            }

            sb.AppendLine("## 6. Licensing & subscription considerations").AppendLine();
            sb.AppendLine("> No prices, tiers or discounts are stated in this report unless they are publicly documented and cited.").AppendLine();
            foreach (var l in r.Licensing)
            {
                sb.AppendLine($"### {l.ProductName}").AppendLine();
                sb.AppendLine(l.Statement + (l.CitationIds.Count > 0 ? " " + string.Join(" ", l.CitationIds.Select(i => $"[{i}]")) : "")).AppendLine();
                foreach (var opt in l.KnownOptions)
                    sb.AppendLine($"- **{opt.Name}** - {opt.Description} *(metric: {opt.LicenseMetric}; disclosure: {opt.Disclosure})*");
                sb.AppendLine().AppendLine("| Sizing input | Count | Basis |").AppendLine("|---|---|---|");
                foreach (var s in l.SizingInputs) sb.AppendLine($"| {s.Persona} | {s.Count?.ToString() ?? "?"} | {s.Basis} |");
                sb.AppendLine();
            }

            sb.AppendLine("## 7. Gaps, risks and implementation considerations").AppendLine();
            foreach (var g in top.Gaps) sb.AppendLine($"- **Gap ({g.Priority}):** {g.Statement}");
            foreach (var w in r.Warnings) sb.AppendLine($"- *{w.Kind}:* {w.Text}");
            foreach (var c in top.Considerations) sb.AppendLine($"- {c}");
            sb.AppendLine();
        }

        sb.AppendLine("## 8. Assumptions").AppendLine();
        foreach (var a in r.Assumptions) sb.AppendLine($"- {a.Text}");
        sb.AppendLine();

        if (r.Clarifications.Count > 0)
        {
            sb.AppendLine("## 9. Open clarification questions").AppendLine();
            foreach (var q in r.Clarifications) sb.AppendLine($"- **{q.Question}** - _{q.WhyItMatters}_");
            sb.AppendLine();
        }

        sb.AppendLine("## Sources").AppendLine();
        foreach (var c in r.Citations)
            sb.AppendLine($"- **[{c.Id}]** {c.DocumentTitle}{(c.Section is null ? "" : $" - {c.Section}")}{(c.Url is null ? "" : $" <{c.Url}>")} · trust: {c.Trust}{(c.IsDemonstrationData ? " · *demonstration data*" : "")}");
        sb.AppendLine();
        sb.AppendLine("## Evidence legend & disclaimer").AppendLine();
        sb.AppendLine("- **Verified** - curator-verified against an official vendor source.");
        sb.AppendLine("- **Document-supported** - backed by a retrieved knowledge passage, pending curator verification.");
        sb.AppendLine("- **Catalogue (unverified)** - catalogue entry with source reference, pending curator verification.");
        sb.AppendLine().AppendLine($"_{r.DataDisclaimer}_");

        return new AdvisoryReport(r.SessionId, title, sb.ToString(), r.Review, DateTimeOffset.UtcNow);
    }

    private static string StatusText(ReviewStatus s) => s switch
    {
        ReviewStatus.Approved => "Approved for customer sharing",
        ReviewStatus.Rejected => "Rejected by reviewer",
        _ => "Draft",
    };

    private static string EvidenceText(EvidenceGrade g) => g switch
    {
        EvidenceGrade.Verified => "Verified",
        EvidenceGrade.DocumentSupported => "Document-supported",
        EvidenceGrade.CatalogueUnverified => "Catalogue (unverified)",
        _ => "**Unsupported**",
    };

    private static string Name(SolutionOption o, string? code) =>
        code is null ? "-" : o.ProductNames[o.ProductCodes.ToList().IndexOf(code)];

    private static void Row(StringBuilder sb, string k, object? v) => sb.AppendLine($"| {k} | {(v is null ? "_not stated_" : Esc(v.ToString()!))} |");

    private static string Esc(string s) => s.Replace("|", "\\|").Replace("\n", " ");

    private static string Pct(double v) => $"{v * 100:0}%";
}
