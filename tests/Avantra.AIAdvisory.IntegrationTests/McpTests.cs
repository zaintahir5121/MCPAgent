using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Avantra.AIAdvisory.IntegrationTests;

/// <summary>Acts exactly like Nexa: an MCP client over Streamable HTTP with service credentials.</summary>
public class McpTests(AdvisoryApiFactory factory) : IClassFixture<AdvisoryApiFactory>
{
    private async Task<McpClient> ConnectAsNexaAsync(string key = "dev-nexa-key")
    {
        var http = factory.CreateClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = key },
            Name = "nexa",
        }, http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    [Fact]
    public async Task Nexa_discovers_advisory_tools()
    {
        await using var client = await ConnectAsNexaAsync();
        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        foreach (var expected in new[] { "list_vendor_domains", "search_vendor_knowledge", "retrieve_product_capabilities", "analyze_customer_requirements",
                     "recommend_vendor_products", "compare_subscription_plans", "evaluate_licensing_requirements", "identify_solution_gaps",
                     "generate_advisory_report", "ask_vendor_knowledge_assistant" })
            Assert.Contains(expected, tools);
        Assert.DoesNotContain(tools, t => t.Contains("ingest") || t.Contains("approve") || t.Contains("publish"));
    }

    [Fact]
    public async Task Nexa_end_to_end_recommend_then_report()
    {
        await using var client = await ConnectAsNexaAsync();
        var rec = await client.CallToolAsync("recommend_vendor_products", new Dictionary<string, object?>
        {
            ["vendorCode"] = "planview",
            ["requirements"] = "2,000 employees, 150 project managers. We need portfolio management, resource management and financial management.",
        });
        Assert.NotEqual(true, rec.IsError);
        using var doc = JsonDocument.Parse(Text(rec));
        var sessionId = doc.RootElement.GetProperty("sessionId").GetString();
        Assert.Equal("Planview Portfolios", doc.RootElement.GetProperty("options")[0].GetProperty("productNames")[0].GetString());
        Assert.Equal("Draft", doc.RootElement.GetProperty("reviewStatus").GetString());

        var report = await client.CallToolAsync("generate_advisory_report", new Dictionary<string, object?> { ["sessionId"] = sessionId });
        Assert.Contains("Solution Advisory Report", Text(report));
    }

    [Fact]
    public async Task Licensing_tool_never_returns_prices()
    {
        await using var client = await ConnectAsNexaAsync();
        var r = await client.CallToolAsync("evaluate_licensing_requirements", new Dictionary<string, object?>
        {
            ["vendorCode"] = "planview", ["productCodes"] = new[] { "planview-portfolios" }, ["projectManagers"] = 150,
        });
        var text = Text(r);
        Assert.Contains("NotPublic", text);
        Assert.DoesNotContain("$", text);
    }

    [Fact]
    public async Task Mcp_requires_authentication()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsNexaAsync("wrong-key"));
    }

    private static string Text(CallToolResult r) => string.Concat(r.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
