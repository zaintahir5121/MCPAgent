using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AvantraAgent.Tests;

public class DiscoveryTests(AgentFactory factory) : IClassFixture<AgentFactory>
{
    private static async Task<JsonObject> Say(HttpClient c, string message, string? chatId = null)
    {
        var res = await c.PostAsJsonAsync("/api/chat", new { message, chatId, knowledgeBase = "planview", discovery = true });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    [Fact]
    public async Task Discovery_builds_profile_pain_points_recommendation_qualification_and_services()
    {
        var c = await factory.ReadyClientAsync();
        var first = await Say(c, "We're a 2,000-person company with 150 project managers and 40 portfolio managers. We have no visibility across our projects and resources are constantly over-allocated.");
        var d1 = first["discovery"]!;
        Assert.Equal("Needs more discovery", d1["qualification"]!["status"]!.GetValue<string>());
        Assert.NotEmpty(d1["painPoints"]!.AsArray());
        Assert.Contains(d1["requirements"]!.AsArray(), r => r!["capability"]!.GetValue<string>() == "Resource management" && r["priority"]!.GetValue<string>() == "Must");
        Assert.NotEmpty(d1["openQuestions"]!.AsArray());

        var chatId = first["chatId"]!.GetValue<string>();
        var second = await Say(c, "We plan in Microsoft Project, teams are hybrid with Scrum teams in Jira, 6 business units, SaaS is fine.", chatId);
        var d2 = second["discovery"]!;
        Assert.Equal("Qualified for solution workshop", d2["qualification"]!["status"]!.GetValue<string>());
        Assert.Contains("Planview Portfolios", d2["recommendation"]!["products"]!.AsArray().Select(p => p!.GetValue<string>()));
        var services = d2["servicesOpportunities"]!.AsArray().Select(s => s!["title"]!.GetValue<string>()).ToList();
        Assert.Contains("Jira integration", services);
        Assert.Contains("Migration from Microsoft Project", services);

        // A question during discovery is answered from the knowledge base and does not change the assessment.
        var q = await Say(c, "What does Planview AgilePlace do?", chatId);
        Assert.Equal("search", q["mode"]!.GetValue<string>());
        Assert.Equal("Qualified for solution workshop", q["discovery"]!["qualification"]!["status"]!.GetValue<string>());

        var summary = await c.GetStringAsync($"/api/discovery/{chatId}/summary");
        Assert.Contains("# Discovery summary — Planview", summary);
        Assert.Contains("## Avantra services opportunities", summary);
        Assert.DoesNotContain("$", summary);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/discovery/unknown-chat")).StatusCode);
    }

    [Fact]
    public async Task Mcp_offers_discovery_and_no_vendor_api_tool()
    {
        var http = await factory.ReadyClientAsync();
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
        }, http, ownsHttpClient: false));
        var tools = (await mcp.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("discovery_chat", tools);
        Assert.Contains("discovery_summary", tools);
        Assert.DoesNotContain(tools, t => t.Contains("api_get"));

        static string Text(CallToolResult r) => string.Concat(r.Content.OfType<TextContentBlock>().Select(x => x.Text));
        var reply = Text(await mcp.CallToolAsync("discovery_chat", new Dictionary<string, object?> { ["message"] = "We have 500 employees and too many projects to prioritise." }));
        Assert.Contains("qualification", reply);
        var chatId = System.Text.Json.JsonDocument.Parse(reply).RootElement.GetProperty("chatId").GetString();
        Assert.Contains("Discovery summary", Text(await mcp.CallToolAsync("discovery_summary", new Dictionary<string, object?> { ["chatId"] = chatId })));
    }
}
