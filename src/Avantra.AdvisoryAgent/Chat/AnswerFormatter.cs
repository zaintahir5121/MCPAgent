using System.Text;
using Avantra.AIAdvisory.Application.Advisory;
using Avantra.AIAdvisory.Application.Knowledge;
using Avantra.AIAdvisory.Domain.Advisory;
using Avantra.AIAdvisory.Domain.Catalogue;

namespace Avantra.AdvisoryAgent.Chat;

/// <summary>Turns structured results into short Markdown chat answers (rendered by the chat page).</summary>
public static class AnswerFormatter
{
    public static string Recommendation(AdvisoryResult r, VendorPack pack)
    {
        var sb = new StringBuilder();
        if (r.Options.Count == 0)
        {
            sb.AppendLine(r.ExecutiveSummary);
            AppendQuestions(sb, r.Clarifications, 4);
            return sb.ToString();
        }
        var top = r.Options[0];
        var musts = r.Profile.Requirements.Count(q => q.Priority == RequirementPriority.Must);
        var mustCovered = top.Coverage.Count(c => c.Requirement.Priority == RequirementPriority.Must && c.ProductCode is not null);
        sb.AppendLine($"### Recommended: {string.Join(" + ", top.ProductNames)}");
        sb.AppendLine($"Covers **{mustCovered} of {musts}** must-have requirements · overall fit **{top.WeightedCoverage * 100:0}%**");
        sb.AppendLine();
        sb.AppendLine("**Why it fits**");
        foreach (var c in top.Coverage)
        {
            var name = pack.Capabilities.FirstOrDefault(x => x.Code == c.Requirement.CapabilityCode)?.Name ?? c.Requirement.CapabilityCode;
            var who = c.ProductCode is null ? "**not covered**" : $"{pack.Products.First(p => p.Code == c.ProductCode).Name} ({Level(c.Coverage)})";
            var cites = c.CitationIds.Count > 0 ? " " + string.Join(" ", c.CitationIds.Select(i => $"[{i}]")) : "";
            sb.AppendLine($"- {(c.ProductCode is null ? "✗" : "✓")} {name} — {who}{(c.Requirement.Origin == RequirementOrigin.Inferred ? " *(inferred)*" : "")}{cites}");
        }
        if (top.AppliedRules.Count > 0)
            sb.AppendLine().AppendLine($"*Avantra delivery experience:* {string.Join("; ", top.AppliedRules.Select(a => a.Description))}");

        if (r.Options.Count > 1)
        {
            sb.AppendLine().AppendLine("**Alternatives**");
            foreach (var o in r.Options.Skip(1))
                sb.AppendLine($"- {string.Join(" + ", o.ProductNames)} — fit {o.WeightedCoverage * 100:0}% ({o.ProductCodes.Count} product{(o.ProductCodes.Count > 1 ? "s" : "")}){(o.Gaps.Count > 0 ? $", missing: {string.Join(", ", o.Gaps.Select(g => CapName(pack, g.CapabilityCode)))}" : "")}");
        }

        if (r.Licensing.Count > 0)
        {
            sb.AppendLine().AppendLine("**Licensing**");
            sb.AppendLine("Pricing, editions and licence types are not public — confirm with Planview or an authorised partner. Quote inputs:");
            var inputs = r.Licensing.SelectMany(l => l.SizingInputs).Where(s => s.Count is not null).DistinctBy(s => s.Persona).ToList();
            sb.AppendLine(inputs.Count == 0 ? "- (none stated yet)" : string.Join("\n", inputs.Select(s => $"- {s.Persona}: {s.Count}")));
        }

        AppendQuestions(sb, r.Clarifications, 3);
        AppendSources(sb, r.Citations);
        sb.AppendLine().AppendLine("_Reply with answers to refine this recommendation, or ask any Planview question._");
        return sb.ToString();
    }

    public static string Search(KnowledgeSearchResult r, string question)
    {
        if (r.Hits.Count == 0)
            return "I couldn't find anything about that in the Planview knowledge base, so I won't guess. Try rephrasing, or describe your organisation's needs and I'll recommend products.";
        var hits = r.Hits.Select(h => (Hit: h, Text: Clean(h.Snippet)))
            .Where(x => x.Text.Length >= 40 && !x.Text.StartsWith("Demonstration content", StringComparison.OrdinalIgnoreCase))
            .Take(3).ToList();
        if (hits.Count == 0)
            return "I couldn't find a clear answer to that in the Planview knowledge base, so I won't guess. Try rephrasing, or describe your organisation's needs and I'll recommend products.";
        var sb = new StringBuilder();
        sb.AppendLine($"Here is what the Planview knowledge base says about **{question.Trim().TrimEnd('?')}**:").AppendLine();
        for (var i = 0; i < hits.Count; i++) sb.AppendLine($"- {hits[i].Text} [S{i + 1}]");
        sb.AppendLine().AppendLine("**Sources**");
        for (var i = 0; i < hits.Count; i++)
            sb.AppendLine($"- [S{i + 1}] {hits[i].Hit.DocumentTitle}{(hits[i].Hit.Section is null || hits[i].Hit.Section == hits[i].Hit.DocumentTitle ? "" : $" — {hits[i].Hit.Section}")}");
        if (r.Hits.Any(h => h.IsDemonstrationData)) sb.AppendLine().AppendLine("_Planview data in this demo is pending verification against official Planview sources._");
        return sb.ToString();
    }

    public static string Comparison(ProductComparison c)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### {string.Join(" vs ", c.ProductNames)}").AppendLine();
        sb.AppendLine("| Capability | " + string.Join(" | ", c.ProductNames) + " |");
        sb.AppendLine("|---|" + string.Join("", c.ProductNames.Select(_ => "---|")));
        foreach (var row in c.Rows)
            sb.AppendLine($"| {row.CapabilityName} | " + string.Join(" | ", c.ProductCodes.Select(p => row.CoverageByProduct[p] switch { "Core" => "✔ Core", "Supported" => "~ Supported", "ViaIntegration" => "↔ Via integration", _ => "–" })) + " |");
        sb.AppendLine().AppendLine("_– means the knowledge base has no evidence for it, not that the product lacks it. Licensing for all products must be confirmed with Planview._");
        return sb.ToString();
    }

    /// <summary>Snippet → one readable sentence block: strips list/quote markers and cuts at a sentence end.</summary>
    internal static string Clean(string snippet)
    {
        var lines = snippet.Replace("\r", "").Split('\n')
            .Select(l => l.Trim().TrimStart('-', '>', '*', '#', ' ').Replace("**", "").Trim())
            .Where(l => l.Length > 0);
        var text = string.Join(" ", lines).Replace("...", "").Trim();
        if (text.Length > 0 && char.IsLower(text[0]))
        {
            // Chunk overlap can start mid-sentence: start at the next sentence instead.
            var start = text.IndexOf(". ", StringComparison.Ordinal);
            if (start > 0 && start < 120) text = text[(start + 2)..];
        }
        if (text.Length > 380)
        {
            var cut = text.LastIndexOf(". ", 380, StringComparison.Ordinal);
            text = cut > 120 ? text[..(cut + 1)] : text[..380] + "…";
        }
        else if (snippet.EndsWith("...", StringComparison.Ordinal))
        {
            var cut = text.LastIndexOf('.');
            if (cut > 60) text = text[..(cut + 1)];
        }
        return text;
    }

    private static void AppendQuestions(StringBuilder sb, IReadOnlyList<ClarificationQuestion> qs, int max)
    {
        if (qs.Count == 0) return;
        sb.AppendLine().AppendLine("**To sharpen this, tell me:**");
        foreach (var (q, i) in qs.Take(max).Select((q, i) => (q, i))) sb.AppendLine($"{i + 1}. {q.Question}");
    }

    private static void AppendSources(StringBuilder sb, IReadOnlyList<Citation> cites)
    {
        if (cites.Count == 0) return;
        sb.AppendLine().AppendLine("**Sources**");
        foreach (var c in cites.Take(8)) sb.AppendLine($"- [{c.Id}] {c.DocumentTitle}{(c.Section is null ? "" : $" — {c.Section}")}");
    }

    private static string Level(CoverageLevel? l) => l switch
    {
        CoverageLevel.Core => "core",
        CoverageLevel.Supported => "supported",
        CoverageLevel.ViaIntegration => "via integration",
        _ => "-",
    };

    private static string CapName(VendorPack pack, string code) => pack.Capabilities.FirstOrDefault(c => c.Code == code)?.Name ?? code;
}
