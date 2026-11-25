using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Domain.Catalogue;
using Avantra.AIAdvisory.KnowledgeBase.Packs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Avantra.AIAdvisory.KnowledgeBase.Ingestion;

/// <summary>
/// Startup job: (1) seeds each vendor pack's documents/ folder through the normal ingestion pipeline
/// (idempotent - unchanged files are skipped), then (2) rebuilds the in-process index from the system of record.
/// In production, seeding is a pipeline/Function job and Azure AI Search is already persistent.
/// </summary>
public sealed class KnowledgeBootstrapper(IServiceScopeFactory scopes, IOptions<KnowledgePackOptions> options, ILogger<KnowledgeBootstrapper> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var catalogue = (FileSystemCatalogueRepository)sp.GetRequiredService<ICatalogueRepository>();
        var ingestion = sp.GetRequiredService<IngestionService>();
        var store = sp.GetRequiredService<IKnowledgeStore>();
        var index = sp.GetRequiredService<IKnowledgeIndex>();

        if (options.Value.SeedDocumentsOnStartup)
        {
            foreach (var vendor in catalogue.GetVendors())
            {
                var folder = Path.Combine(catalogue.PacksRoot, vendor.Code, "documents");
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.GetFiles(folder).Order())
                {
                    await using var stream = File.OpenRead(file);
                    var result = await ingestion.IngestAsync(new IngestionRequest(vendor.Code, Path.GetFileName(file), null, stream,
                        PublishImmediately: true), ct);
                    if (result.Outcome != IngestionOutcome.Unchanged)
                        logger.LogInformation("Seeded {File}: {Outcome}", Path.GetFileName(file), result.Outcome);
                }
            }
        }

        if (index is Indexing.InMemoryHybridIndex)
        {
            var docs = await store.ListPublishedWithChunksAsync(ct);
            foreach (var d in docs) await index.IndexAsync(d, ct);
            logger.LogInformation("In-memory knowledge index rebuilt: {Docs} documents, {Chunks} chunks", docs.Count, docs.Sum(d => d.Chunks.Count));
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
