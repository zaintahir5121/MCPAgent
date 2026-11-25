using System.ComponentModel;
using Avantra.AdvisoryAgent.Chat;
using Avantra.AdvisoryAgent.Connectors.Planview;
using Avantra.AIAdvisory.Domain.Advisory;
using ModelContextProtocol.Server;

namespace Avantra.AdvisoryAgent.Mcp;

/// <summary>
/// MCP tools exposed by the Advisory Agent. Names are prefixed per connector (planview_*) so one MCP client page
/// can hold the tools of all connectors (salesforce_*, outsystems_* later) without clashes.
/// </summary>
[McpServerToolType]
public sealed class PlanviewMcpTools
{
    [McpServerTool(Name = "ask_advisor", Title = "Ask the Advisory Agent", ReadOnly = false, Idempotent = false, OpenWorld = false)]
    [Description("Agentic entry point: ask anything in plain words (e.g. 'We have 2,000 staff and 150 PMs, which Planview products?'). The agent picks the tools. Pass chatId back to continue the same conversation.")]
    public static Task<ChatReply> AskAdvisor(ChatService chat, [Description("The question or the customer's needs")] string message,
        [Description("chatId from a previous answer, to continue")] string? chatId = null, CancellationToken ct = default)
        => chat.ReplyAsync(new ChatRequest(message, chatId), ct);

    [McpServerTool(Name = "planview_search_knowledge", Title = "Search Planview knowledge", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Search planview-index (Azure AI Search or local). Returns passages with source title and section.")]
    public static async Task<object> SearchKnowledge(PlanviewConnector planview, [Description("Search text")] string query,
        [Description("Max results 1-20")] int top = 5, [Description("Optional product code, e.g. planview-portfolios")] string? productCode = null, CancellationToken ct = default)
    {
        var r = await planview.SearchAsync(query, top, productCode, ct);
        return new { r.Query, index = "planview-index", hits = r.Hits.Select(h => new { h.DocumentTitle, h.Section, h.Snippet, h.Score, h.SourceUrl, h.ProductCodes }), r.Notice };
    }

    [McpServerTool(Name = "planview_recommend_products", Title = "Recommend Planview products", ReadOnly = false, Idempotent = false, OpenWorld = false)]
    [Description("Recommend Planview products for customer needs: requirement profile, ranked options, coverage with sources, gaps, licensing quote inputs (never prices), clarifying questions. Pass sessionId to refine.")]
    public static async Task<object> RecommendProducts(PlanviewConnector planview, [Description("Customer needs in plain words")] string requirements,
        [Description("sessionId from a previous call")] string? sessionId = null, CancellationToken ct = default)
    {
        var r = await planview.RecommendAsync(requirements, Guid.TryParse(sessionId, out var g) ? g : null, ct);
        return new
        {
            r.SessionId,
            summary = r.ExecutiveSummary,
            answer = AnswerFormatter.Recommendation(r, planview.Pack),
            options = r.Options.Select(o => new { o.Label, o.ProductNames, o.MustHaveCoverage, o.WeightedCoverage, gaps = o.Gaps.Select(x => x.CapabilityCode) }),
            clarifyingQuestions = r.Clarifications.Select(c => c.Question),
            r.Citations,
            r.DataDisclaimer,
        };
    }

    [McpServerTool(Name = "planview_compare_products", Title = "Compare Planview products", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Compare two or more Planview products capability by capability, with licensing notes.")]
    public static object CompareProducts(PlanviewConnector planview, [Description("Product names or codes, e.g. Portfolios, AdaptiveWork")] string[] products)
    {
        var c = planview.Compare(products);
        return new { c.ProductNames, c.Rows, table = AnswerFormatter.Comparison(c), c.Notice };
    }

    [McpServerTool(Name = "planview_licensing_guidance", Title = "Planview licensing guidance", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Licensing guidance for Planview products: what is public, what to confirm with Planview, and quote inputs from headcount. Never returns prices.")]
    public static object LicensingGuidance(PlanviewConnector planview, [Description("Product names or codes")] string[] products,
        int? employees = null, int? projectManagers = null, int? portfolioManagers = null, int? resourceManagers = null, int? teamMembers = null) =>
        planview.Licensing(products, new OrganizationProfile
        {
            Employees = employees, ProjectManagers = projectManagers, PortfolioManagers = portfolioManagers,
            ResourceManagers = resourceManagers, TeamMembers = teamMembers,
        });

    [McpServerTool(Name = "planview_list_products", Title = "List Planview products", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List Planview products in the catalogue with their capabilities and verification status.")]
    public static object ListProducts(PlanviewConnector planview) => planview.ListProducts();

    [McpServerTool(Name = "planview_api_get", Title = "Read live Planview data", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Agentic live data: read-only GET to the configured Planview API (relative path). Returns a 'not configured' message if no Planview API is set up.")]
    public static Task<string> ApiGet(PlanviewApiClient api, [Description("Relative API path")] string path, CancellationToken ct = default) => api.GetAsync(path, ct);
}
