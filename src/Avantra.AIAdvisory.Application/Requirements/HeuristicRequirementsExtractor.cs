using System.Globalization;
using System.Text.RegularExpressions;
using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Domain.Advisory;
using Avantra.AIAdvisory.Domain.Catalogue;

namespace Avantra.AIAdvisory.Application.Requirements;

/// <summary>
/// Deterministic requirements extractor. Always available (no model needed), used as a fallback when the AI
/// provider is not configured or fails, and as a cross-check for numbers the LLM extracts.
/// </summary>
public sealed partial class HeuristicRequirementsExtractor : IRequirementsExtractor
{
    public string Name => "heuristic";

    [GeneratedRegex(@"(?<num>\d{1,3}(?:[,\s]\d{3})+|\d+(?:\.\d+)?)\s*(?<k>k\b)?\s*\+?\s*-?\s*(?<what>employees|employee|staff|people|person|personnel|workers|project\s+managers|pms|portfolio\s+managers|resource\s+managers|business\s+units|bus\b|divisions|executives|team\s+members|contributors|engineers|developers)", RegexOptions.IgnoreCase)]
    private static partial Regex CountPattern();

    [GeneratedRegex(@"(?<=[.!?;\n])\s+|\n")]
    private static partial Regex SentenceSplit();

    private static readonly string[] MustMarkers = ["must", "need", "needs", "require", "requires", "required", "critical", "mandatory", "essential", "have to"];
    private static readonly string[] CouldMarkers = ["nice to have", "would like", "optionally", "optional", "could", "maybe", "in the future", "eventually", "later"];

    private static readonly string[] KnownSystems =
        ["Jira", "Azure DevOps", "ServiceNow", "SAP", "Salesforce", "Workday", "Oracle", "Microsoft Project", "MS Project", "Microsoft Teams", "MS Teams", "Power BI", "Smartsheet", "Excel", "GitHub", "Tableau", "Dynamics 365"];

    public Task<RequirementProfile?> ExtractAsync(string text, VendorPack pack, CancellationToken ct)
    {
        text ??= string.Empty;
        var org = ExtractOrganization(text);
        var requirements = new List<Requirement>();
        var unmapped = new List<string>();

        var sentences = SentenceSplit().Split(text).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        foreach (var sentence in sentences)
        {
            var lower = sentence.ToLowerInvariant();
            var priority = DetectPriority(lower);
            var matchedAny = false;
            foreach (var cap in pack.Capabilities)
            {
                var hit = Terms(cap).FirstOrDefault(t => ContainsTerm(lower, t));
                if (hit is null) continue;
                matchedAny = true;
                var existing = requirements.FindIndex(r => r.CapabilityCode == cap.Code);
                if (existing >= 0)
                {
                    // Keep the statement where the customer expressed the strongest need.
                    if (requirements[existing].Priority < priority)
                        requirements[existing] = requirements[existing] with { Priority = priority, Statement = $"{cap.Name} (from: \"{Trim(sentence)}\")" };
                    continue;
                }
                requirements.Add(new Requirement($"R{requirements.Count + 1}", $"{cap.Name} (from: \"{Trim(sentence)}\")", cap.Code, priority, RequirementOrigin.Stated));
            }

            if (!matchedAny && LooksLikeRequirement(lower))
                unmapped.Add(Trim(sentence));
        }

        // Inference: methodology mentions imply agile capabilities, but only as "Should" and flagged as inferred.
        if (org.Methodology is "agile" or "hybrid")
            AddInferred(requirements, pack, "agile-planning", "Agile delivery mentioned - agile planning capability inferred.");
        if (org.ExistingSystems.Any(s => s is "Jira" or "Azure DevOps" or "ServiceNow" or "GitHub"))
            AddInferred(requirements, pack, "toolchain-integration", $"Existing delivery tools ({string.Join(", ", org.ExistingSystems)}) must stay in sync with planning - integration inferred.");
        if ((org.ProjectManagers ?? 0) > 0 && requirements.Exists(r => r.CapabilityCode == "resource-management"))
            AddInferred(requirements, pack, "time-tracking", "Resource management for many project managers usually requires time/actuals capture.");

        return Task.FromResult<RequirementProfile?>(new RequirementProfile(pack.Vendor.Code, org, requirements, unmapped, Name));
    }

    internal static OrganizationProfile ExtractOrganization(string text)
    {
        int? employees = null, pms = null, portfolio = null, rms = null, bus = null, execs = null, team = null;
        foreach (Match m in CountPattern().Matches(text))
        {
            var raw = m.Groups["num"].Value.Replace(",", "").Replace(" ", "");
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) continue;
            if (m.Groups["k"].Success) n *= 1000;
            var value = (int)Math.Round(n);
            var what = m.Groups["what"].Value.ToLowerInvariant();
            switch (what)
            {
                case var w when w.StartsWith("project"): pms ??= value; break;
                case "pms": pms ??= value; break;
                case var w when w.StartsWith("portfolio"): portfolio ??= value; break;
                case var w when w.StartsWith("resource"): rms ??= value; break;
                case var w when w.StartsWith("business") || w is "bus" or "divisions": bus ??= value; break;
                case "executives": execs ??= value; break;
                case var w when w.StartsWith("team") || w is "contributors" or "engineers" or "developers": team ??= value; break;
                default: employees ??= value; break;
            }
        }

        var lower = text.ToLowerInvariant();
        if (bus is null && (lower.Contains("multiple business units") || lower.Contains("several business units") || lower.Contains("multiple divisions")))
            bus = null; // explicitly unknown count - clarification will ask

        var agile = ContainsAny(lower, "agile", "scrum", "safe", "kanban", "sprint", "devops");
        var traditional = ContainsAny(lower, "waterfall", "traditional", "pmo", "stage-gate", "stage gate", "prince2", "pmbok");
        string? methodology = (agile, traditional) switch
        {
            (true, true) => "hybrid",
            (true, false) => "agile",
            (false, true) => "traditional",
            _ => null,
        };
        if (lower.Contains("hybrid")) methodology = "hybrid";

        string? deployment = null;
        if (ContainsAny(lower, "on-prem", "on prem", "on-premises", "self-hosted", "self hosted")) deployment = "on-premises";
        else if (ContainsAny(lower, "saas", "cloud-based", "cloud based", "in the cloud", "hosted")) deployment = "saas";

        var systems = KnownSystems.Where(s => ContainsTerm(lower, s.ToLowerInvariant())).Distinct().ToList();

        return new OrganizationProfile
        {
            Employees = employees,
            ProjectManagers = pms,
            PortfolioManagers = portfolio,
            ResourceManagers = rms,
            BusinessUnits = bus,
            Executives = execs,
            TeamMembers = team,
            Methodology = methodology,
            DeploymentPreference = deployment,
            ExistingSystems = systems,
        };
    }

    private static void AddInferred(List<Requirement> list, VendorPack pack, string capabilityCode, string why)
    {
        if (list.Exists(r => r.CapabilityCode == capabilityCode)) return;
        if (!pack.Capabilities.Any(c => c.Code == capabilityCode)) return;
        list.Add(new Requirement($"R{list.Count + 1}", why, capabilityCode, RequirementPriority.Should, RequirementOrigin.Inferred));
    }

    private static IEnumerable<string> Terms(Capability cap) =>
        new[] { cap.Name.ToLowerInvariant() }.Concat(cap.Synonyms.Select(s => s.ToLowerInvariant()));

    private static RequirementPriority DetectPriority(string lower)
    {
        if (CouldMarkers.Any(lower.Contains)) return RequirementPriority.Could;
        if (MustMarkers.Any(m => ContainsTerm(lower, m))) return RequirementPriority.Must;
        return RequirementPriority.Should;
    }

    private static bool LooksLikeRequirement(string lower) =>
        lower.Length > 15 && (MustMarkers.Any(m => ContainsTerm(lower, m)) || ContainsAny(lower, "want", "looking for", "support", "ability to"));

    private static bool ContainsAny(string text, params string[] terms) => terms.Any(t => ContainsTerm(text, t));

    public static bool ContainsTerm(string text, string term)
    {
        var idx = 0;
        while ((idx = text.IndexOf(term, idx, StringComparison.Ordinal)) >= 0)
        {
            var before = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
            var end = idx + term.Length;
            var after = end >= text.Length || !char.IsLetterOrDigit(text[end]) || (end + 1 <= text.Length && text[end] == 's' && (end + 1 == text.Length || !char.IsLetterOrDigit(text[end + 1])));
            if (before && after) return true;
            idx = end;
        }
        return false;
    }

    private static string Trim(string s) => s.Length <= 160 ? s : s[..157] + "...";
}
