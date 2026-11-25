using System.Text.Json;
using System.Text.Json.Serialization;
using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Application.Evidence;
using Avantra.AIAdvisory.Application.Knowledge;
using Avantra.AIAdvisory.Application.Licensing;
using Avantra.AIAdvisory.Application.Recommendation;
using Avantra.AIAdvisory.Application.Requirements;
using Avantra.AIAdvisory.Application.Safety;
using Avantra.AIAdvisory.Domain.Advisory;
using Avantra.AIAdvisory.Domain.Catalogue;
using Avantra.AIAdvisory.Domain.Governance;
using Microsoft.Extensions.Logging;

namespace Avantra.AIAdvisory.Application.Advisory;

/// <summary>
/// The advisory workflow. This is a deterministic, auditable pipeline with AI used at two bounded points
/// (requirements extraction and executive-summary wording). It is intentionally NOT an autonomous agent loop:
/// every recommendation can be reproduced from (input, pack version) and explained line by line.
/// </summary>
public sealed class AdvisoryOrchestrator(
    ICatalogueRepository catalogue,
    RequirementsAnalyzer analyzer,
    ClarificationService clarifications,
    RecommendationEngine engine,
    EvidenceService evidence,
    LicensingEvaluator licensing,
    INarrativeGenerator narrative,
    ISessionStore sessions,
    IAuditLog audit,
    IUserContext user,
    IAiProviderInfo ai,
    ILogger<AdvisoryOrchestrator> logger)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public const int MaxInputLength = 8000;

    public async Task<AdvisoryResult> AdviseAsync(AdvisoryRequest request, CancellationToken ct)
    {
        var pack = catalogue.GetPack(request.VendorCode) ?? throw new UnknownVendorException(request.VendorCode);
        var input = Validate(request.Requirements);

        var session = await LoadOrCreateSessionAsync(request.SessionId, pack.Vendor.Code, ct);
        session.Turns.Add(new SessionTurn { Ordinal = session.Turns.Count + 1, Role = "user", Content = input });
        var conversation = string.Join("\n", session.Turns.Where(t => t.Role == "user").Select(t => t.Content));

        var warnings = new List<AdvisoryStatement>();
        if (ContentSafety.LooksLikePromptInjection(input, out var injection))
            warnings.Add(new("security", $"Input contained instruction-like text ('{injection}'). It was treated as customer data only."));

        var profile = await analyzer.AnalyzeAsync(conversation, pack, ct);
        var questions = clarifications.GetQuestions(profile, pack);
        var registry = new CitationRegistry();

        var options = new List<SolutionOption>();
        foreach (var option in engine.Recommend(profile, pack))
            options.Add(await evidence.AttachEvidenceAsync(option, pack, registry, ct));

        var licensingAssessments = options.Count > 0
            ? licensing.Evaluate(options[0].ProductCodes, profile, pack, registry)
            : [];

        var assumptions = BuildAssumptions(profile, pack);
        warnings.AddRange(BuildWarnings(profile, options, pack));

        var draft = new AdvisoryResult(session.Id, pack.Vendor.Code, DateTimeOffset.UtcNow, profile, questions, options,
            licensingAssessments, registry.All, assumptions, warnings, null, "none", ReviewStatus.Draft, pack.Vendor.DataDisclaimer);

        var (summary, source) = await BuildSummaryAsync(draft, pack, ct);
        var result = draft with { ExecutiveSummary = summary, ExecutiveSummarySource = source };

        session.Turns.Add(new SessionTurn { Ordinal = session.Turns.Count + 1, Role = "advisor", Content = summary });
        session.LatestResultJson = JsonSerializer.Serialize(result, Json);
        session.Review = ReviewStatus.Draft;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await sessions.SaveAsync(session, ct);

        await audit.WriteAsync(new AuditEntry
        {
            TenantId = user.TenantId, ActorId = user.UserId, Channel = user.Channel, Action = "advisory.run",
            ResourceId = session.Id.ToString(), ModelUsed = ai.ChatEnabled ? ai.ChatModel : "none",
            Detail = $"pack={pack.Vendor.PackVersion}; analyzer={profile.AnalyzerUsed}; options={string.Join("|", options.Select(o => string.Join("+", o.ProductCodes)))}; summary={source}",
        }, ct);

        logger.LogInformation("Advisory session {SessionId} produced {Options} options using {Analyzer}", session.Id, options.Count, profile.AnalyzerUsed);
        return result;
    }

    /// <summary>Requirements analysis + clarification questions only (no recommendation, nothing persisted).</summary>
    public async Task<RequirementAnalysis> AnalyzeAsync(string vendorCode, string requirements, CancellationToken ct)
    {
        var pack = catalogue.GetPack(vendorCode) ?? throw new UnknownVendorException(vendorCode);
        var text = Validate(requirements);
        var profile = await analyzer.AnalyzeAsync(text, pack, ct);
        return new RequirementAnalysis(profile, clarifications.GetQuestions(profile, pack),
            profile.Requirements.Select(r => pack.Capabilities.First(c => c.Code == r.CapabilityCode)).DistinctBy(c => c.Code).ToList());
    }

    /// <summary>Gap analysis of a given product set (e.g. what the customer already licenses) against the requirements.</summary>
    public async Task<GapAnalysis> AnalyzeGapsAsync(string vendorCode, string requirements, IReadOnlyList<string> productCodes, CancellationToken ct)
    {
        var pack = catalogue.GetPack(vendorCode) ?? throw new UnknownVendorException(vendorCode);
        var profile = await analyzer.AnalyzeAsync(Validate(requirements), pack, ct);
        var unknown = productCodes.Where(c => !pack.Products.Any(p => string.Equals(p.Code, c, StringComparison.OrdinalIgnoreCase))).ToList();
        var registry = new CitationRegistry();
        var option = await evidence.AttachEvidenceAsync(engine.EvaluateProducts(profile, pack, productCodes), pack, registry, ct);
        var best = engine.Recommend(profile, pack, new RecommendationOptions(MaxOptions: 1)).FirstOrDefault();
        var additions = best is null ? [] : best.ProductCodes.Except(option.ProductCodes).ToList();
        return new GapAnalysis(profile, option, unknown, additions, registry.All, pack.Vendor.DataDisclaimer);
    }

    private static string Validate(string? requirements)
    {
        var text = (requirements ?? string.Empty).Trim();
        if (text.Length == 0) throw new ArgumentException("Requirements text is required.");
        if (text.Length > MaxInputLength) throw new ArgumentException($"Requirements text exceeds {MaxInputLength} characters.");
        return ContentSafety.RedactSensitive(text);
    }

    public async Task<AdvisoryResult?> GetLatestAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await sessions.GetAsync(sessionId, user.TenantId, ct);
        if (session?.LatestResultJson is null) return null;
        var result = JsonSerializer.Deserialize<AdvisoryResult>(session.LatestResultJson, Json)!;
        return result with { Review = session.Review };
    }

    public async Task<AdvisoryResult?> ReviewAsync(Guid sessionId, ReviewDecision decision, CancellationToken ct)
    {
        if (!user.IsInRole(Roles.AdvisoryReviewer))
            throw new UnauthorizedAccessException("Only reviewers can approve advisory results.");
        var session = await sessions.GetAsync(sessionId, user.TenantId, ct);
        if (session?.LatestResultJson is null) return null;
        session.Review = decision.Approve ? ReviewStatus.Approved : ReviewStatus.Rejected;
        session.ReviewedBy = user.UserId;
        session.ReviewedAt = DateTimeOffset.UtcNow;
        session.ReviewComment = decision.Comment;
        await sessions.SaveAsync(session, ct);
        await audit.WriteAsync(new AuditEntry
        {
            TenantId = user.TenantId, ActorId = user.UserId, Channel = user.Channel,
            Action = decision.Approve ? "advisory.approve" : "advisory.reject", ResourceId = sessionId.ToString(), Detail = decision.Comment,
        }, ct);
        return await GetLatestAsync(sessionId, ct);
    }

    private async Task<AdvisorySession> LoadOrCreateSessionAsync(Guid? id, string vendorCode, CancellationToken ct)
    {
        if (id is { } existingId)
        {
            var existing = await sessions.GetAsync(existingId, user.TenantId, ct);
            if (existing is not null)
            {
                if (!string.Equals(existing.VendorCode, vendorCode, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("A session is bound to a single vendor domain.");
                return existing;
            }
        }
        return new AdvisorySession { TenantId = user.TenantId, OwnerId = user.UserId, VendorCode = vendorCode, Channel = user.Channel };
    }

    private async Task<(string Summary, string Source)> BuildSummaryAsync(AdvisoryResult draft, VendorPack pack, CancellationToken ct)
    {
        var deterministic = DeterministicSummary(draft, pack);
        if (!ai.ChatEnabled || draft.Options.Count == 0) return (deterministic, "deterministic-template");
        try
        {
            var facts = JsonSerializer.Serialize(FactSheet(draft, pack), Json);
            var text = await narrative.GenerateExecutiveSummaryAsync(facts, ct);
            if (text is not null)
            {
                if (NarrativeGuard.IsAcceptable(text, pack, draft.Citations.Select(c => c.Id).ToList(), out var reason))
                    return (text.Trim(), "ai-grounded");
                logger.LogWarning("AI narrative rejected by guard: {Reason}", reason);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Narrative generation failed; using deterministic summary.");
        }
        return (deterministic, "deterministic-template");
    }

    /// <summary>The ONLY facts the narrative model may use.</summary>
    internal static object FactSheet(AdvisoryResult r, VendorPack pack) => new
    {
        vendor = pack.Vendor.Name,
        organization = r.Profile.Organization,
        requirements = r.Profile.Requirements.Select(q => new { q.Id, q.Statement, q.CapabilityCode, priority = q.Priority.ToString(), origin = q.Origin.ToString() }),
        options = r.Options.Select(o => new
        {
            o.Label, products = o.ProductNames, mustHaveCoverage = o.MustHaveCoverage, weightedCoverage = o.WeightedCoverage,
            coverage = o.Coverage.Select(c => new { c.Requirement.Id, c.Explanation, evidence = c.Evidence.ToString(), citations = c.CitationIds }),
            gaps = o.Gaps.Select(g => g.Statement),
            avantraRules = o.AppliedRules.Select(a => a.Description),
        }),
        licensing = r.Licensing.Select(l => new { l.ProductName, l.Statement, l.RequiresVendorConfirmation }),
        openQuestions = r.Clarifications.Select(c => c.Question),
        dataDisclaimer = r.DataDisclaimer,
    };

    internal static string DeterministicSummary(AdvisoryResult r, VendorPack pack)
    {
        if (r.Options.Count == 0)
            return r.Profile.Requirements.Count == 0
                ? $"We could not yet identify requirements that map to the {pack.Vendor.Name} capability model. Please answer the clarification questions so a recommendation can be prepared."
                : $"None of the {pack.Vendor.Name} products in the knowledge base cover the stated requirements. A specialist review is recommended.";

        var top = r.Options[0];
        var musts = r.Profile.Requirements.Count(q => q.Priority == RequirementPriority.Must);
        var text = $"Based on {r.Profile.Requirements.Count} identified requirement(s) ({musts} must-have), the recommended {pack.Vendor.Name} solution is " +
                   $"{string.Join(" + ", top.ProductNames)}, covering {Pct(top.MustHaveCoverage)} of must-have and {Pct(top.WeightedCoverage)} of weighted requirements.";
        if (top.Gaps.Count > 0) text += $" {top.Gaps.Count} requirement(s) are not covered by this option and need a specialist decision.";
        if (r.Options.Count > 1) text += $" {r.Options.Count - 1} alternative option(s) are provided for comparison.";
        if (r.Licensing.Any(l => l.RequiresVendorConfirmation)) text += $" Licensing, editions and pricing are not publicly confirmed and must be validated with {pack.Vendor.Name} or an authorised partner.";
        if (r.Clarifications.Count > 0) text += $" {r.Clarifications.Count} clarification question(s) remain open and may change the recommendation.";
        return text;
    }

    private static List<AdvisoryStatement> BuildAssumptions(RequirementProfile p, VendorPack pack)
    {
        var list = new List<AdvisoryStatement>
        {
            new("data", $"Recommendations use {pack.Vendor.Name} knowledge pack version {pack.Vendor.PackVersion}."),
        };
        foreach (var r in p.Requirements.Where(r => r.Origin == RequirementOrigin.Inferred))
            list.Add(new("inferred-requirement", $"{r.Id} was inferred, not stated: {r.Statement}"));
        if (p.Organization.Methodology is null)
            list.Add(new("methodology", "Delivery methodology was not stated; no agile/traditional fit rules were applied."));
        if (p.Organization.DeploymentPreference is null)
            list.Add(new("deployment", "Deployment preference was not stated; SaaS availability has not been used as a filter."));
        return list;
    }

    private static IEnumerable<AdvisoryStatement> BuildWarnings(RequirementProfile p, IReadOnlyList<SolutionOption> options, VendorPack pack)
    {
        if (pack.Products.Any(x => x.Verification != VerificationStatus.Verified))
            yield return new("data-quality", pack.Vendor.DataDisclaimer);
        if (options.Count > 0)
            foreach (var g in options[0].Gaps.Where(g => g.Priority == RequirementPriority.Must))
                yield return new("gap", $"Must-have requirement not covered by the recommended option: {g.Statement}");
        foreach (var u in p.UnmappedStatements.Take(5))
            yield return new("unmapped", $"Not mapped to the capability model (needs specialist review): {u}");
        foreach (var rule in options.SelectMany(o => o.AppliedRules).Where(r => r.RequiresBusinessConfirmation).DistinctBy(r => r.Code))
            yield return new("avantra-rule", $"Avantra advisory heuristic applied and awaiting business confirmation: {rule.Description}");
    }

    private static string Pct(double v) => $"{v * 100:0}%";
}
