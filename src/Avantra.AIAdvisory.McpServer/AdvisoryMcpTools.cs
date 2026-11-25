using System.ComponentModel;
using Avantra.AIAdvisory.Agents;
using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Application.Advisory;
using Avantra.AIAdvisory.Application.Knowledge;
using Avantra.AIAdvisory.Application.Licensing;
using Avantra.AIAdvisory.Application.Evidence;
using Avantra.AIAdvisory.Application.Reporting;
using Avantra.AIAdvisory.Domain.Advisory;
using Avantra.AIAdvisory.Domain.Catalogue;
using ModelContextProtocol.Server;

namespace Avantra.AIAdvisory.McpServer;

/// <summary>
/// MCP tool surface consumed by Nexa and other MCP-capable assistants. Every tool is a THIN adapter over an
/// application service - no business logic lives here, so the REST API, the web UI and MCP always agree.
/// Not exposed via MCP (by design): ingestion, publishing, approval/review and administration.
/// </summary>
[McpServerToolType]
public sealed class AdvisoryMcpTools
{
    [McpServerTool(Name = "list_vendor_domains", Title = "List vendor domains", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the technology vendor domains (e.g. planview) the advisory platform can advise on, with data disclaimers. Call first if unsure which vendorCode to use.")]
    public static IReadOnlyList<Vendor> ListVendorDomains(ICatalogueRepository catalogue) => catalogue.GetVendors();

    [McpServerTool(Name = "search_vendor_knowledge", Title = "Search vendor knowledge", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Hybrid search over the published, vendor-isolated knowledge base. Returns passages with source title, URL, section and trust level. Use for factual questions; cite the returned sources. Never answer vendor facts without a result.")]
    public static Task<KnowledgeSearchResult> SearchVendorKnowledge(
        KnowledgeSearchService search,
        [Description("Vendor domain code, e.g. 'planview'.")] string vendorCode,
        [Description("Natural-language search query.")] string query,
        [Description("Max results (1-20). Default 5.")] int top = 5,
        [Description("Optional product code filter, e.g. 'planview-portfolios'.")] string? productCode = null,
        [Description("Minimum source trust: Unverified, ThirdParty, AvantraInternal, PartnerVerified, VendorOfficial.")] SourceTrustLevel minimumTrust = SourceTrustLevel.Unverified,
        CancellationToken ct = default)
        => search.SearchAsync(vendorCode, query, top, productCode, minimumTrust, ct);

    [McpServerTool(Name = "retrieve_product_capabilities", Title = "Retrieve product capabilities", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns catalogue entries (capabilities with coverage level, deployment, implementation considerations, sources, verification status). Omit productCode to list all products of the vendor.")]
    public static object RetrieveProductCapabilities(
        ICatalogueRepository catalogue,
        [Description("Vendor domain code, e.g. 'planview'.")] string vendorCode,
        [Description("Optional product code or name.")] string? productCode = null)
    {
        var pack = catalogue.GetPack(vendorCode) ?? throw new UnknownVendorException(vendorCode);
        var products = productCode is null
            ? pack.Products
            : pack.Products.Where(p => string.Equals(p.Code, productCode, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(p.Name, productCode, StringComparison.OrdinalIgnoreCase)
                                       || p.Aliases.Contains(productCode, StringComparer.OrdinalIgnoreCase)).ToList();
        return new
        {
            vendor = pack.Vendor.Name,
            packVersion = pack.Vendor.PackVersion,
            notice = pack.Vendor.DataDisclaimer,
            capabilityModel = pack.Capabilities.Select(c => new { c.Code, c.Name, c.Category }),
            products,
        };
    }

    [McpServerTool(Name = "analyze_customer_requirements", Title = "Analyze customer requirements", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Extracts a structured requirement profile (organisation size, personas, methodology, requirements mapped to the vendor capability model with priority) and returns clarification questions to ask the user. Does not recommend products.")]
    public static Task<RequirementAnalysis> AnalyzeCustomerRequirements(
        AdvisoryOrchestrator orchestrator,
        [Description("Vendor domain code, e.g. 'planview'.")] string vendorCode,
        [Description("The customer's requirements in natural language.")] string requirements,
        CancellationToken ct = default)
        => orchestrator.AnalyzeAsync(vendorCode, requirements, ct);

    [McpServerTool(Name = "recommend_vendor_products", Title = "Recommend vendor products", ReadOnly = false, Idempotent = false, OpenWorld = false)]
    [Description("Runs the full advisory pipeline: requirement analysis, deterministic product/bundle recommendation with requirement-by-requirement coverage, evidence grading and citations, licensing considerations (never prices), assumptions, warnings and clarification questions. Returns a sessionId - pass it back with additional answers to refine the recommendation, and to generate a report. Present results as DRAFT until approved by an Avantra reviewer.")]
    public static async Task<object> RecommendVendorProducts(
        AdvisoryOrchestrator orchestrator,
        [Description("Vendor domain code, e.g. 'planview'.")] string vendorCode,
        [Description("Customer requirements, or answers to previous clarification questions when sessionId is provided.")] string requirements,
        [Description("Optional sessionId from a previous call to continue the same advisory conversation.")] string? sessionId = null,
        CancellationToken ct = default)
    {
        var result = await orchestrator.AdviseAsync(new AdvisoryRequest(vendorCode, requirements, ParseSession(sessionId)), ct);
        return Compact(result);
    }

    [McpServerTool(Name = "compare_subscription_plans", Title = "Compare products and subscription options", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Side-by-side comparison of products: capability coverage matrix plus known licensing/subscription options with disclosure status and the sizing inputs that matter. Does not state prices; flags what must be confirmed with the vendor.")]
    public static ProductComparison CompareSubscriptionPlans(
        ProductComparisonService comparison,
        [Description("Vendor domain code, e.g. 'planview'.")] string vendorCode,
        [Description("Two or more product codes or names to compare.")] string[] productCodes,
        [Description("Optional capability codes to restrict the comparison.")] string[]? capabilityCodes = null)
        => comparison.Compare(vendorCode, productCodes, capabilityCodes);

    [McpServerTool(Name = "evaluate_licensing_requirements", Title = "Evaluate licensing requirements", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("For the given products and headcount, returns licensing considerations: known licence options, disclosure status, sizing inputs by persona and whether vendor confirmation is required. Never returns or estimates prices.")]
    public static IReadOnlyList<LicensingAssessment> EvaluateLicensingRequirements(
        ICatalogueRepository catalogue,
        LicensingEvaluator licensing,
        [Description("Vendor domain code, e.g. 'planview'.")] string vendorCode,
        [Description("Product codes to evaluate.")] string[] productCodes,
        int? employees = null, int? projectManagers = null, int? portfolioManagers = null,
        int? resourceManagers = null, int? teamMembers = null, int? executives = null)
    {
        var pack = catalogue.GetPack(vendorCode) ?? throw new UnknownVendorException(vendorCode);
        var profile = new RequirementProfile(pack.Vendor.Code, new OrganizationProfile
        {
            Employees = employees, ProjectManagers = projectManagers, PortfolioManagers = portfolioManagers,
            ResourceManagers = resourceManagers, TeamMembers = teamMembers, Executives = executives,
        }, [], [], "none");
        return licensing.Evaluate(productCodes, profile, pack, new CitationRegistry());
    }

    [McpServerTool(Name = "identify_solution_gaps", Title = "Identify solution gaps", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Gap analysis: evaluates a given product set (e.g. what the customer already owns) against their requirements, returning covered/uncovered requirements with evidence, and products that would close the gaps.")]
    public static Task<GapAnalysis> IdentifySolutionGaps(
        AdvisoryOrchestrator orchestrator,
        [Description("Vendor domain code, e.g. 'planview'.")] string vendorCode,
        [Description("Customer requirements in natural language.")] string requirements,
        [Description("Product codes the customer has or is considering.")] string[] productCodes,
        CancellationToken ct = default)
        => orchestrator.AnalyzeGapsAsync(vendorCode, requirements, productCodes, ct);

    [McpServerTool(Name = "generate_advisory_report", Title = "Generate advisory report", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Generates the professional advisory report (Markdown) for a session created by recommend_vendor_products. Reports are marked DRAFT until an Avantra reviewer approves the session.")]
    public static async Task<object> GenerateAdvisoryReport(
        AdvisoryOrchestrator orchestrator,
        ICatalogueRepository catalogue,
        AdvisoryReportGenerator reports,
        [Description("sessionId returned by recommend_vendor_products.")] string sessionId,
        [Description("Optional customer name for the report title.")] string? customerName = null,
        CancellationToken ct = default)
    {
        var id = ParseSession(sessionId) ?? throw new ArgumentException("sessionId is required.");
        var result = await orchestrator.GetLatestAsync(id, ct) ?? throw new ArgumentException($"Session {sessionId} not found for this tenant.");
        var vendor = catalogue.GetPack(result.VendorCode)?.Vendor.Name ?? result.VendorCode;
        var report = reports.Generate(result, vendor, customerName);
        return new { report.SessionId, report.Title, report.Review, report.GeneratedAt, report.Markdown, downloadPath = $"/api/v1/advisory/sessions/{id}/report" };
    }

    [McpServerTool(Name = "ask_vendor_knowledge_assistant", Title = "Ask the knowledge assistant", ReadOnly = true, Idempotent = false, OpenWorld = false)]
    [Description("Open question answering over the vendor knowledge base by a retrieval-grounded agent (e.g. 'What is the difference between AgilePlace and AdaptiveWork?'). For recommendations use recommend_vendor_products instead.")]
    public static Task<AssistantAnswer> AskVendorKnowledgeAssistant(
        KnowledgeAssistantAgent assistant,
        [Description("Vendor domain code, e.g. 'planview'.")] string vendorCode,
        [Description("The question.")] string question,
        CancellationToken ct = default)
        => assistant.AskAsync(vendorCode, question, ct);

    private static Guid? ParseSession(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? null : Guid.TryParse(sessionId, out var g) ? g : throw new ArgumentException("sessionId must be a GUID.");

    /// <summary>Chat-friendly projection: enough for a conversational answer, with full detail available via the REST API.</summary>
    internal static object Compact(AdvisoryResult r) => new
    {
        r.SessionId,
        reviewStatus = r.Review.ToString(),
        r.ExecutiveSummary,
        r.ExecutiveSummarySource,
        organization = r.Profile.Organization,
        requirements = r.Profile.Requirements.Select(q => new { q.Id, q.Statement, q.CapabilityCode, priority = q.Priority.ToString(), origin = q.Origin.ToString() }),
        options = r.Options.Select(o => new
        {
            o.Label, o.ProductNames, o.MustHaveCoverage, o.WeightedCoverage, o.Score,
            coverage = o.Coverage.Select(c => new { requirement = c.Requirement.Id, c.ProductCode, coverage = c.Coverage?.ToString() ?? "GAP", evidence = c.Evidence.ToString(), c.CitationIds }),
            gaps = o.Gaps.Select(g => g.Statement),
            avantraRules = o.AppliedRules.Select(a => a.Description),
            o.Considerations,
        }),
        licensing = r.Licensing.Select(l => new { l.ProductName, l.Statement, l.RequiresVendorConfirmation, l.SizingInputs }),
        clarificationQuestions = r.Clarifications.Select(c => new { c.Question, c.WhyItMatters }),
        r.Assumptions,
        r.Warnings,
        citations = r.Citations,
        r.DataDisclaimer,
        nextSteps = new[]
        {
            "Ask the user the clarification questions and call recommend_vendor_products again with the same sessionId.",
            "Call generate_advisory_report with the sessionId to produce the shareable report.",
        },
    };
}
