using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.KnowledgeBase.Indexing;
using Avantra.AIAdvisory.KnowledgeBase.Ingestion;
using Avantra.AIAdvisory.KnowledgeBase.Packs;
using Avantra.AIAdvisory.KnowledgeBase.Parsing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Avantra.AIAdvisory.KnowledgeBase;

public static class DependencyInjection
{
    public static IServiceCollection AddKnowledgePlatform(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<KnowledgePackOptions>(configuration.GetSection(KnowledgePackOptions.Section));
        services.Configure<AzureAiSearchOptions>(configuration.GetSection(AzureAiSearchOptions.Section));

        services.AddSingleton<ICatalogueRepository, FileSystemCatalogueRepository>();
        services.AddSingleton<IDocumentParser, MarkdownParser>();
        services.AddSingleton<IDocumentParser, HtmlParser>();
        services.AddSingleton<IDocumentParser, PdfParser>();
        services.AddSingleton<IDocumentParser, DocxParser>();
        services.AddSingleton<IDocumentParser, PlainTextParser>();
        services.AddSingleton<DocumentParserRegistry>();
        services.AddScoped<IngestionService>();

        var provider = configuration[$"{KnowledgePackOptions.Section}:IndexProvider"] ?? "InMemory";
        if (string.Equals(provider, "AzureAiSearch", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IKnowledgeIndex, AzureAiSearchIndex>();
        else
            services.AddSingleton<IKnowledgeIndex, InMemoryHybridIndex>();

        services.AddHostedService<KnowledgeBootstrapper>();
        return services;
    }
}
