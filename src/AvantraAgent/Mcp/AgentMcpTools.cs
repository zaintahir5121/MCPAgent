using System.ComponentModel;
using AvantraAgent.Advisory;
using AvantraAgent.Chat;
using AvantraAgent.Connectors.Planview;
using Avantra.AIAdvisory.Domain.Advisory;
using ModelContextProtocol.Server;

namespace AvantraAgent.Mcp;

/// <summary>
/// MCP tools exposed by AvantraAgent. Every knowledge base added at runtime is reachable through the same tools
/// (pass its code), so MCP clients never need new tools when knowledge grows.
/// Creating knowledge bases and ingesting documents are deliberately NOT exposed via MCP (REST admin API only).
/// </summary>
[McpServerToolType]
public sealed class AgentMcpTools
{
    [McpServerTool(Name = "ask_advisor", Title = "Ask AvantraAgent", ReadOnly = false, Idempotent = false, OpenWorld = false)]
    [Description("Ask anything in plain words, e.g. 'We have 2,000 staff and 150 PMs, which Planview products?'. The agent picks the knowledge bases and tools. Pass chatId back to continue the conversation.")]
    public static Task<ChatReply> AskAdvisor(ChatService chat,
        [Description("The question or the customer's needs")] string message,
        [Description("chatId from a previous answer, to continue")] string? chatId = null,
        [Description("Knowledge base code to focus on, or \"all\" (default)")] string? knowledgeBase = null,
        CancellationToken ct = default)
        => chat.ReplyAsync(new ChatRequest(message, chatId, knowledgeBase), ct);

    [McpServerTool(Name = "knowledge_list", Title = "List knowledge bases", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List all knowledge bases (code, name, description) and whether product advisory is available for each.")]
    public static object KnowledgeList(AdvisoryTools tools) =>
        tools.KnowledgeBases.Select(k => new { k.Code, k.Name, k.Description, advisory = tools.HasAdvisory(k.Code) });

    [McpServerTool(Name = "knowledge_search", Title = "Search knowledge", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Search one knowledge base (its own index) or \"all\". Returns passages with document title, section and knowledge base.")]
    public static Task<IReadOnlyList<KnowledgeHitView>> KnowledgeSearch(AdvisoryTools tools,
        [Description("Search text")] string query,
        [Description("Knowledge base code, or \"all\"")] string knowledgeBase = "all",
        [Description("Max results 1-20")] int top = 5, CancellationToken ct = default)
        => tools.SearchAsync(knowledgeBase, query, top, ct);

    [McpServerTool(Name = "advisory_recommend", Title = "Recommend products", ReadOnly = false, Idempotent = false, OpenWorld = false)]
    [Description("Recommend products for customer needs (knowledge bases with product advisory, e.g. planview): ranked options, coverage with sources, gaps, licensing quote inputs (never prices), clarifying questions. Pass sessionId to refine.")]
    public static async Task<object> Recommend(AdvisoryTools tools,
        [Description("Knowledge base code, e.g. planview")] string knowledgeBase,
        [Description("Customer needs in plain words")] string requirements,
        [Description("sessionId from a previous call")] string? sessionId = null, CancellationToken ct = default)
    {
        var code = tools.Normalize(knowledgeBase);
        var r = await tools.RecommendAsync(code, requirements, Guid.TryParse(sessionId, out var g) ? g : null, ct);
        return new
        {
            r.SessionId,
            answer = AnswerFormatter.Recommendation(r, tools.Pack(code)),
            options = r.Options.Select(o => new { o.Label, o.ProductNames, o.MustHaveCoverage, o.WeightedCoverage, gaps = o.Gaps.Select(x => x.CapabilityCode) }),
            clarifyingQuestions = r.Clarifications.Select(c => c.Question),
            r.Citations,
            r.DataDisclaimer,
        };
    }

    [McpServerTool(Name = "advisory_compare", Title = "Compare products", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Compare two or more products of a knowledge base capability by capability.")]
    public static object Compare(AdvisoryTools tools, [Description("Knowledge base code")] string knowledgeBase,
        [Description("Product names or codes")] string[] products)
    {
        var c = tools.Compare(tools.Normalize(knowledgeBase), products);
        return new { c.ProductNames, c.Rows, table = AnswerFormatter.Comparison(c), c.Notice };
    }

    [McpServerTool(Name = "advisory_licensing", Title = "Licensing guidance", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Licensing guidance: what is public, what to confirm with the vendor, and quote inputs from headcount. Never returns prices.")]
    public static object Licensing(AdvisoryTools tools, [Description("Knowledge base code")] string knowledgeBase,
        [Description("Product names or codes")] string[] products,
        int? employees = null, int? projectManagers = null, int? portfolioManagers = null, int? resourceManagers = null, int? teamMembers = null) =>
        tools.Licensing(tools.Normalize(knowledgeBase), products, new OrganizationProfile
        {
            Employees = employees, ProjectManagers = projectManagers, PortfolioManagers = portfolioManagers,
            ResourceManagers = resourceManagers, TeamMembers = teamMembers,
        });

    [McpServerTool(Name = "advisory_list_products", Title = "List products", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List the products of a knowledge base that has product advisory.")]
    public static object Products(AdvisoryTools tools, [Description("Knowledge base code")] string knowledgeBase) =>
        tools.ListProducts(tools.Normalize(knowledgeBase));

    [McpServerTool(Name = "planview_api_get", Title = "Read live Planview data", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Live data: read-only GET to the configured Planview API (relative path). Says 'not configured' when no Planview API is set up.")]
    public static Task<string> PlanviewApi(PlanviewApiClient api, [Description("Relative API path")] string path, CancellationToken ct = default) => api.GetAsync(path, ct);
}
