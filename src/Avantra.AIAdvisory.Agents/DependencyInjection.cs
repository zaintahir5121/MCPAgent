using Avantra.AIAdvisory.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Avantra.AIAdvisory.Agents;

public static class DependencyInjection
{
    public static IServiceCollection AddAdvisoryAgents(this IServiceCollection services)
    {
        // Registered after the heuristic extractor; RequirementsAnalyzer tries AI extractors first, then merges.
        services.AddSingleton<IRequirementsExtractor, LlmRequirementsExtractor>();
        services.AddSingleton<INarrativeGenerator, GroundedNarrativeGenerator>();
        services.AddScoped<KnowledgeAssistantAgent>();
        return services;
    }
}
