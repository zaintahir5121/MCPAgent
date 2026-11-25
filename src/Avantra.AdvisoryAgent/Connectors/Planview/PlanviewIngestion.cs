using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.KnowledgeBase.Indexing;
using Avantra.AIAdvisory.KnowledgeBase.Ingestion;
using Avantra.AIAdvisory.KnowledgeBase.Packs;
using Microsoft.Extensions.Options;

namespace Avantra.AdvisoryAgent.Connectors.Planview;

public sealed record IngestedFile(string File, string Outcome, string Status, int Chunks, string? Message);

public sealed record IngestionRun(
    Guid RunId, string Trigger, string IndexName, string SourceFolder, DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
    int Files, int Created, int Updated, int Unchanged, int Quarantined, int Failed, IReadOnlyList<IngestedFile> Details);

public sealed record IngestionStatus(string IndexName, string SearchProvider, bool Running, IngestionRun? LastRun, DateTimeOffset? NextScheduledRun, double IntervalHours);

/// <summary>Ingests the Planview source folder into planview-index. One run at a time; used by the API and the schedule.</summary>
public sealed class PlanviewIngestionJob(
    IServiceScopeFactory scopes, ConnectorIndexRouter router, IOptions<PlanviewConnectorOptions> options,
    IOptions<ConnectorIndexOptions> search, ILogger<PlanviewIngestionJob> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public IngestionRun? LastRun { get; private set; }
    public DateTimeOffset? NextScheduledRun { get; set; }
    public bool Running => _gate.CurrentCount == 0;

    public string SourceFolder => FileSystemCatalogueRepository.ResolvePath(options.Value.SourceFolder);

    public IngestionStatus Status() => new(router.IndexNameFor(PlanviewConnector.VendorCode), search.Value.Provider, Running, LastRun, NextScheduledRun, options.Value.IngestionIntervalHours);

    public async Task<IngestionRun> RunAsync(string trigger, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) throw new IngestionBusyException();
        try
        {
            var started = DateTimeOffset.UtcNow;
            var details = new List<IngestedFile>();
            var folder = SourceFolder;
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Planview source folder not found: {folder}");

            using var scope = scopes.CreateScope();
            var ingestion = scope.ServiceProvider.GetRequiredService<IngestionService>();
            foreach (var file in Directory.GetFiles(folder).Where(IsSupported).Order())
            {
                try
                {
                    await using var stream = File.OpenRead(file);
                    var r = await ingestion.IngestAsync(new IngestionRequest(PlanviewConnector.VendorCode, Path.GetFileName(file), null, stream, PublishImmediately: true), ct);
                    details.Add(new IngestedFile(Path.GetFileName(file), r.Outcome.ToString(), r.Status.ToString(), r.Chunks, r.Message));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Failed to ingest {File}", file);
                    details.Add(new IngestedFile(Path.GetFileName(file), "Failed", "-", 0, ex.Message));
                }
            }

            var run = new IngestionRun(Guid.NewGuid(), trigger, router.IndexNameFor(PlanviewConnector.VendorCode), folder, started, DateTimeOffset.UtcNow,
                details.Count, Count("Created"), Count("NewVersion"), Count("Unchanged"), Count("Quarantined"), Count("Failed"), details);
            LastRun = run;
            logger.LogInformation("Planview ingestion ({Trigger}): {Files} files, {Created} new, {Updated} updated, {Unchanged} unchanged, {Quarantined} quarantined, {Failed} failed",
                trigger, run.Files, run.Created, run.Updated, run.Unchanged, run.Quarantined, run.Failed);
            return run;
            int Count(string outcome) => details.Count(d => d.Outcome == outcome);
        }
        finally { _gate.Release(); }
    }

    private static bool IsSupported(string f) =>
        new[] { ".md", ".markdown", ".txt", ".html", ".htm", ".pdf", ".docx" }.Contains(Path.GetExtension(f).ToLowerInvariant());
}

public sealed class IngestionBusyException() : Exception("A Planview ingestion run is already in progress.");

/// <summary>
/// Background schedule: on start, reloads planview-index from the database (in-memory mode), optionally runs an
/// ingestion, then re-ingests every IngestionIntervalHours (unchanged files are skipped by content hash).
/// </summary>
public sealed class PlanviewIngestionScheduler(
    PlanviewIngestionJob job, ConnectorIndexRouter router, IServiceScopeFactory scopes,
    IOptions<PlanviewConnectorOptions> options, ILogger<PlanviewIngestionScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            if (!router.IsPersistent)
            {
                using var scope = scopes.CreateScope();
                var docs = await scope.ServiceProvider.GetRequiredService<IKnowledgeStore>().ListPublishedWithChunksAsync(ct);
                foreach (var d in docs.Where(d => d.VendorCode == PlanviewConnector.VendorCode)) await router.IndexAsync(d, ct);
                logger.LogInformation("planview-index loaded from database: {Docs} documents", docs.Count);
            }
            if (options.Value.RunIngestionOnStartup) await job.RunAsync("startup", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Startup ingestion failed"); }

        var hours = options.Value.IngestionIntervalHours;
        if (hours <= 0) return;
        var interval = TimeSpan.FromHours(hours);
        while (!ct.IsCancellationRequested)
        {
            job.NextScheduledRun = DateTimeOffset.UtcNow.Add(interval);
            try { await Task.Delay(interval, ct); } catch (OperationCanceledException) { return; }
            try { await job.RunAsync("schedule", ct); }
            catch (IngestionBusyException) { logger.LogInformation("Scheduled ingestion skipped: a run is already in progress."); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Scheduled ingestion failed"); }
        }
    }
}
