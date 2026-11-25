using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;

namespace Avantra.AIAdvisory.McpServer;

public static class McpServerRegistration
{
    public static readonly JsonSerializerOptions ToolJson = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var o = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    /// <summary>
    /// Streamable HTTP MCP server, stateless mode: every request is independent and horizontally scalable;
    /// conversational state lives in the platform (sessionId), not in the MCP transport.
    /// </summary>
    public static IServiceCollection AddAdvisoryMcpServer(this IServiceCollection services)
    {
        services.AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "avantra-ai-advisory", Version = "1.0.0", Title = "Avantra AI Advisory Platform" };
                o.ServerInstructions =
                    "Avantra technology advisory tools. Use list_vendor_domains to find a vendorCode. For product recommendations use " +
                    "recommend_vendor_products and keep the returned sessionId for follow-ups. Always show citations and the data disclaimer. " +
                    "Never state prices or licensing tiers that the tools did not return; results are DRAFT until reviewed by Avantra.";
            })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<AdvisoryMcpTools>(ToolJson);
        return services;
    }

    public static IEndpointConventionBuilder MapAdvisoryMcp(this IEndpointRouteBuilder endpoints, string pattern = "/mcp") =>
        endpoints.MapMcp(pattern);
}
