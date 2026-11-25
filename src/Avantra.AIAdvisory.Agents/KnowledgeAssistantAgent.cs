using System.ComponentModel;
using System.Text;
using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Application.Knowledge;
using Avantra.AIAdvisory.Application.Safety;
using Avantra.AIAdvisory.Domain.Catalogue;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Avantra.AIAdvisory.Agents;

public sealed record AssistantAnswer(string Answer, string Mode, IReadOnlyList<string> SourcesConsulted, string Notice);

/// <summary>
/// Free-form Q&amp;A agent built on Microsoft Agent Framework (<see cref="ChatClientAgent"/>) with retrieval tools.
/// It is used for open questions ("Does AgilePlace support WIP limits?"). Structured advisory work goes through the
/// deterministic AdvisoryOrchestrator instead - the agent has no tool that can produce recommendations or prices.
/// </summary>
public sealed class KnowledgeAssistantAgent(
    IServiceProvider services,
    KnowledgeSearchService search,
    ICatalogueRepository catalogue,
    ILoggerFactory loggerFactory)
{
    public async Task<AssistantAnswer> AskAsync(string vendorCode, string question, CancellationToken ct)
    {
        var pack = catalogue.GetPack(vendorCode) ?? throw new UnknownVendorException(vendorCode);
        var consulted = new List<string>();
        var chat = services.GetService<IChatClient>();

        if (chat is null)
        {
            // No model configured: return the evidence itself (still useful, never fabricated).
            var result = await search.SearchAsync(pack.Vendor.Code, question, 3, null, SourceTrustLevel.Unverified, ct);
            var sb = new StringBuilder("AI provider not configured - showing the most relevant knowledge passages:\n");
            foreach (var h in result.Hits) { sb.AppendLine($"\n- {h.DocumentTitle} ({h.Section}): {h.Snippet}"); consulted.Add(h.DocumentTitle); }
            if (result.Hits.Count == 0) sb.AppendLine("No matching knowledge found.");
            return new AssistantAnswer(sb.ToString(), "retrieval-only", consulted, result.Notice);
        }

        [Description("Search the published, vendor-isolated knowledge base. Returns passages with source titles.")]
        async Task<string> SearchKnowledge([Description("Search query")] string query, [Description("Optional product code filter")] string? productCode = null)
        {
            var r = await search.SearchAsync(pack.Vendor.Code, query, 5, productCode, SourceTrustLevel.Unverified, ct);
            consulted.AddRange(r.Hits.Select(h => h.DocumentTitle));
            if (r.Hits.Count == 0) return "NO_RESULTS";
            return string.Join("\n", r.Hits.Select(h => ContentSafety.AsUntrustedData($"{h.DocumentTitle} / {h.Section}", h.Snippet))) + $"\nNOTICE: {r.Notice}";
        }

        [Description("List products in the vendor catalogue with their capability codes.")]
        string ListProducts() => string.Join("\n", pack.Products.Select(p =>
            $"{p.Code}: {p.Name} - {p.Summary} [capabilities: {string.Join(", ", p.Capabilities.Select(c => $"{c.CapabilityCode}({c.Coverage})"))}] [verification: {p.Verification}]"));

        var agent = new ChatClientAgent(chat,
            instructions: Prompts.KnowledgeAssistant.Replace("{vendor}", pack.Vendor.Name),
            name: "AvantraKnowledgeAssistant",
            description: $"Answers {pack.Vendor.Name} product questions from the Avantra knowledge base.",
            tools: [AIFunctionFactory.Create(SearchKnowledge, "search_knowledge"), AIFunctionFactory.Create(ListProducts, "list_products")],
            loggerFactory: loggerFactory);

        var response = await agent.RunAsync(question, cancellationToken: ct);
        var answer = response.Text;
        var notice = pack.Vendor.DataDisclaimer;
        if (ContentSafety.ContainsPriceStatement(answer))
        {
            answer = "The generated answer contained pricing statements that are not supported by verified sources and was withheld. " +
                     $"Pricing and licensing must be confirmed with {pack.Vendor.Name} or an authorised partner.";
            notice = "Answer withheld by output guard (pricing).";
        }
        return new AssistantAnswer(answer, "agent", consulted.Distinct().ToList(), notice);
    }
}
