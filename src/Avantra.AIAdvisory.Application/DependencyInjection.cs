using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Application.Advisory;
using Avantra.AIAdvisory.Application.Evidence;
using Avantra.AIAdvisory.Application.Knowledge;
using Avantra.AIAdvisory.Application.Licensing;
using Avantra.AIAdvisory.Application.Recommendation;
using Avantra.AIAdvisory.Application.Reporting;
using Avantra.AIAdvisory.Application.Requirements;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Avantra.AIAdvisory.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddAdvisoryApplication(this IServiceCollection services)
    {
        services.TryAddSingleton<IKnowledgeBaseRegistry, PackKnowledgeBaseRegistry>();
        services.AddSingleton<HeuristicRequirementsExtractor>();
        services.AddSingleton<IRequirementsExtractor>(sp => sp.GetRequiredService<HeuristicRequirementsExtractor>());
        services.AddScoped<RequirementsAnalyzer>();
        services.AddSingleton<ClarificationService>();
        services.AddSingleton<RecommendationEngine>();
        services.AddSingleton<LicensingEvaluator>();
        services.AddSingleton<AdvisoryReportGenerator>();
        services.AddScoped<EvidenceService>();
        services.AddScoped<KnowledgeSearchService>();
        services.AddScoped<ProductComparisonService>();
        services.AddScoped<AdvisoryOrchestrator>();
        return services;
    }
}
