using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Domain.Catalogue;
using Avantra.AIAdvisory.Domain.Knowledge;
using Avantra.AIAdvisory.Infrastructure.Persistence;
using Avantra.AIAdvisory.KnowledgeBase.Indexing;
using Avantra.AIAdvisory.KnowledgeBase.Ingestion;
using Avantra.AIAdvisory.KnowledgeBase.Packs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AvantraAgent.Knowledge;

public sealed record StagedFile(string FileName, string Path, string? ContentType, string? SourceUrl);

public sealed record IngestionWorkItem(Guid JobId, string KnowledgeBase, string Kind, IReadOnlyList<StagedFile> Files, string? Folder, bool Publish, SourceTrustLevel Trust,
    CrawlConfig? Crawl = null, int? MaxPages = null, bool Force = false);

public sealed record IngestedItem(string Name, string Outcome, string Status, int Chunks, string? Message);

/// <summary>Accepts ingestion work, records it as a job in the database and hands it to the background worker.</summary>
public sealed class IngestionQueue(IServiceScopeFactory scopes, IOptions<IngestionOptions> options)
{
    // Two lanes: website crawls can run for hours (crawl delays), so they never block uploads and folder syncs.
    private readonly Channel<IngestionWorkItem> _standard = Channel.CreateBounded<IngestionWorkItem>(
        new BoundedChannelOptions(Math.Max(10, options.Value.QueueCapacity)) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Channel<IngestionWorkItem> _crawl = Channel.CreateBounded<IngestionWorkItem>(
        new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly ConcurrentDictionary<Guid, bool> _cancelRequests = new();

    public ChannelReader<IngestionWorkItem> Reader(bool crawlLane) => crawlLane ? _crawl.Reader : _standard.Reader;

    public void RequestCancel(Guid jobId) => _cancelRequests[jobId] = true;
    public bool IsCancelRequested(Guid jobId) => _cancelRequests.ContainsKey(jobId);
    public void ClearCancel(Guid jobId) => _cancelRequests.TryRemove(jobId, out _);

    public async Task<IngestionJobRecord> EnqueueAsync(string kb, string kind, string trigger, IReadOnlyList<StagedFile> files, string? folder,
        bool publish, SourceTrustLevel trust, string requestedBy, CancellationToken ct, CrawlConfig? crawl = null, int? maxPages = null, bool force = false)
    {
        var job = new IngestionJobRecord { KnowledgeBaseCode = kb, Kind = kind, Trigger = trigger, Items = files.Count, RequestedBy = requestedBy };
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AdvisoryDbContext>();
            db.IngestionJobs.Add(job);
            await db.SaveChangesAsync(ct);
        }
        var lane = kind == "crawl" ? _crawl : _standard;
        if (!lane.Writer.TryWrite(new IngestionWorkItem(job.Id, kb, kind, files, folder, publish, trust, crawl, maxPages, force)))
        {
            await UpdateAsync(job.Id, j => { j.State = IngestionJobState.Failed; j.Error = "Ingestion queue is full. Try again later."; j.FinishedAt = DateTimeOffset.UtcNow; }, ct);
            foreach (var f in files) TryDelete(f.Path);
            throw new QueueFullException();
        }
        return job;
    }

    public async Task<bool> HasPendingJobAsync(string kb, string kind, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AdvisoryDbContext>();
        return await db.IngestionJobs.AnyAsync(j => j.KnowledgeBaseCode == kb && j.Kind == kind &&
            (j.State == IngestionJobState.Queued || j.State == IngestionJobState.Running), ct);
    }

    internal async Task UpdateAsync(Guid id, Action<IngestionJobRecord> change, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AdvisoryDbContext>();
        var job = await db.IngestionJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return;
        change(job);
        await db.SaveChangesAsync(ct);
    }

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class QueueFullException() : Exception("The ingestion queue is full. Try again later.");

/// <summary>Standard lane: uploads, URL documents and folder syncs.</summary>
public sealed class IngestionWorker(IngestionQueue queue, IServiceScopeFactory scopes, EfKnowledgeBaseRegistry registry, WebsiteCrawler crawler, ILogger<IngestionWorker> logger)
    : IngestionWorkerBase(queue, scopes, registry, crawler, logger, crawlLane: false);

/// <summary>Crawl lane: website crawls (long-running, polite).</summary>
public sealed class CrawlWorker(IngestionQueue queue, IServiceScopeFactory scopes, EfKnowledgeBaseRegistry registry, WebsiteCrawler crawler, ILogger<CrawlWorker> logger)
    : IngestionWorkerBase(queue, scopes, registry, crawler, logger, crawlLane: true);

/// <summary>Processes ingestion jobs of one lane one at a time (parse → screen → chunk → embed → store → index).</summary>
public abstract class IngestionWorkerBase(
    IngestionQueue queue, IServiceScopeFactory scopes, EfKnowledgeBaseRegistry registry, WebsiteCrawler crawler, ILogger logger, bool crawlLane) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Jobs that were queued/running when the service stopped do not resume (their staged input may be gone).
        if (!crawlLane)
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AdvisoryDbContext>();
            var stale = await db.IngestionJobs.Where(j => j.State == IngestionJobState.Queued || j.State == IngestionJobState.Running).ToListAsync(stoppingToken);
            foreach (var j in stale)
            {
                j.State = IngestionJobState.Interrupted;
                j.FinishedAt = DateTimeOffset.UtcNow;
                j.Error = "Service restarted before the job finished. Please resubmit.";
            }
            if (stale.Count > 0) await db.SaveChangesAsync(stoppingToken);
        }

        await foreach (var item in queue.Reader(crawlLane).ReadAllAsync(stoppingToken))
        {
            try
            {
                if (queue.IsCancelRequested(item.JobId))
                {
                    await queue.UpdateAsync(item.JobId, j => { j.State = IngestionJobState.Cancelled; j.FinishedAt = DateTimeOffset.UtcNow; }, stoppingToken);
                    foreach (var f in item.Files) IngestionQueue.TryDelete(f.Path);
                    continue;
                }
                if (item.Crawl is not null) await CrawlAsync(item, stoppingToken);
                else await ProcessAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ingestion job {JobId} failed", item.JobId);
                await queue.UpdateAsync(item.JobId, j => { j.State = IngestionJobState.Failed; j.Error = ex.Message; j.FinishedAt = DateTimeOffset.UtcNow; }, CancellationToken.None);
                foreach (var f in item.Files) IngestionQueue.TryDelete(f.Path);
            }
            finally { queue.ClearCancel(item.JobId); }
        }
    }

    private async Task CrawlAsync(IngestionWorkItem item, CancellationToken ct)
    {
        await queue.UpdateAsync(item.JobId, j => { j.State = IngestionJobState.Running; j.StartedAt = DateTimeOffset.UtcNow; }, ct);
        void Apply(IngestionJobRecord j, CrawlProgress p)
        {
            j.Items = p.Discovered; j.Created = p.Created; j.Updated = p.Updated; j.Unchanged = p.Unchanged;
            j.Quarantined = p.Quarantined; j.Failed = p.Failed;
        }
        var (progress, results) = await crawler.CrawlAsync(item.KnowledgeBase, item.Crawl!, item.MaxPages, item.Force,
            p => queue.UpdateAsync(item.JobId, j => Apply(j, p), ct), () => queue.IsCancelRequested(item.JobId), ct);
        var cancelled = queue.IsCancelRequested(item.JobId);
        await queue.UpdateAsync(item.JobId, j =>
        {
            Apply(j, progress);
            j.DetailsJson = JsonSerializer.Serialize(results.Select(r => new IngestedItem(r.Url, r.Outcome, "-", 0, r.Message)), Json);
            var notes = new List<string>();
            if (progress.Restricted > 0) notes.Add($"{progress.Restricted} pages skipped because they need a sign-in or matched a skip phrase");
            if (progress.Skipped > 0) notes.Add($"{progress.Skipped} eligible pages not fetched (page limit reached or cancelled); they are picked up by the next crawl");
            j.Error = notes.Count > 0 ? string.Join(". ", notes) + "." : null;
            j.State = cancelled ? IngestionJobState.Cancelled
                : progress.Failed == 0 ? IngestionJobState.Succeeded
                : progress.Failed < progress.Fetched ? IngestionJobState.PartiallySucceeded : IngestionJobState.Failed;
            j.FinishedAt = DateTimeOffset.UtcNow;
        }, CancellationToken.None);
        await registry.UpdateAsync(item.KnowledgeBase, k => k.LastCrawledAt = DateTimeOffset.UtcNow, CancellationToken.None);
        logger.LogInformation("Crawl job {JobId} for {Kb}: {Discovered} eligible, {Fetched} fetched, {Created} new, {Updated} updated, {Unchanged} unchanged, {Failed} failed",
            item.JobId, item.KnowledgeBase, progress.Discovered, progress.Fetched, progress.Created, progress.Updated, progress.Unchanged, progress.Failed);
    }

    private async Task ProcessAsync(IngestionWorkItem item, CancellationToken ct)
    {
        await queue.UpdateAsync(item.JobId, j => { j.State = IngestionJobState.Running; j.StartedAt = DateTimeOffset.UtcNow; }, ct);
        var files = item.Files.ToList();
        var fromFolder = item.Folder is not null;
        if (fromFolder)
        {
            if (!Directory.Exists(item.Folder)) throw new DirectoryNotFoundException($"Source folder not found: {item.Folder}");
            files = Directory.GetFiles(item.Folder!, "*", SearchOption.AllDirectories)
                .Where(f => IngestionOptions.SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Order().Select(f => new StagedFile(Path.GetRelativePath(item.Folder!, f), f, null, null)).ToList();
            await queue.UpdateAsync(item.JobId, j => j.Items = files.Count, ct);
        }

        var results = new List<IngestedItem>();
        using var scope = scopes.CreateScope();
        var ingestion = scope.ServiceProvider.GetRequiredService<IngestionService>();
        foreach (var f in files)
        {
            if (queue.IsCancelRequested(item.JobId)) { results.Add(new IngestedItem(f.FileName, "Cancelled", "-", 0, null)); if (!fromFolder) IngestionQueue.TryDelete(f.Path); continue; }
            try
            {
                await using var stream = File.OpenRead(f.Path);
                var r = await ingestion.IngestAsync(new IngestionRequest(item.KnowledgeBase, Path.GetFileName(f.FileName), f.ContentType, stream,
                    ExternalId: fromFolder ? ExternalIdFor(f.FileName) : null, SourceUrl: f.SourceUrl, Trust: item.Trust, PublishImmediately: item.Publish), ct);
                results.Add(new IngestedItem(f.FileName, r.Outcome.ToString(), r.Status.ToString(), r.Chunks, r.Message));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to ingest {File} into {Kb}", f.FileName, item.KnowledgeBase);
                results.Add(new IngestedItem(f.FileName, "Failed", "-", 0, ex.Message));
            }
            finally
            {
                if (!fromFolder) IngestionQueue.TryDelete(f.Path);
            }
        }

        int Count(string outcome) => results.Count(r => r.Outcome == outcome);
        var pending = results.Count(r => r.Status == nameof(DocumentStatus.PendingReview) && r.Outcome != "Unchanged");
        var failed = Count("Failed");
        await queue.UpdateAsync(item.JobId, j =>
        {
            j.Items = results.Count;
            j.Created = Count("Created");
            j.Updated = Count("NewVersion");
            j.Unchanged = Count("Unchanged");
            j.Quarantined = Count("Quarantined");
            j.Failed = failed;
            j.PendingReview = pending;
            j.DetailsJson = JsonSerializer.Serialize(results, Json);
            j.State = queue.IsCancelRequested(item.JobId) ? IngestionJobState.Cancelled
                : failed == 0 ? IngestionJobState.Succeeded : failed < results.Count ? IngestionJobState.PartiallySucceeded : IngestionJobState.Failed;
            j.FinishedAt = DateTimeOffset.UtcNow;
        }, ct);
        if (fromFolder) await registry.UpdateAsync(item.KnowledgeBase, k => k.LastSyncedAt = DateTimeOffset.UtcNow, ct);
        logger.LogInformation("Ingestion job {JobId} ({Kind}) into {Kb}: {Items} items, {Created} new, {Updated} updated, {Unchanged} unchanged, {Quarantined} quarantined, {Failed} failed",
            item.JobId, item.Kind, item.KnowledgeBase, results.Count, Count("Created"), Count("NewVersion"), Count("Unchanged"), Count("Quarantined"), failed);
    }

    /// <summary>Folder files keep a stable id per relative path so edits create new versions instead of duplicates.</summary>
    private static string ExternalIdFor(string relativePath) =>
        Path.ChangeExtension(relativePath, null).Replace('\\', '/').Replace('/', '-').ToLowerInvariant();
}

/// <summary>
/// Startup + schedule: loads each knowledge base's index (in-memory mode) and queues a folder sync for knowledge bases
/// whose SyncIntervalHours has elapsed. Checks every minute.
/// </summary>
public sealed class KnowledgeSyncScheduler(
    IngestionQueue queue, EfKnowledgeBaseRegistry registry, ConnectorIndexRouter router, KnowledgeReadiness readiness,
    IServiceScopeFactory scopes, IOptions<IngestionOptions> options, ILogger<KnowledgeSyncScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            if (!router.IsPersistent)
            {
                using var scope = scopes.CreateScope();
                var docs = await scope.ServiceProvider.GetRequiredService<IKnowledgeStore>().ListPublishedWithChunksAsync(ct);
                foreach (var d in docs) await router.IndexAsync(d, ct);
                logger.LogInformation("Local indexes loaded from database: {Docs} documents", docs.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Loading local indexes failed"); }
        finally { readiness.MarkReady(); }

        var first = true;
        while (!ct.IsCancellationRequested)
        {
            foreach (var kb in registry.All.Where(k => k.IsEnabled && !string.IsNullOrWhiteSpace(k.SourceFolder)))
            {
                var due = kb.SyncIntervalHours > 0 && (kb.LastSyncedAt is null || DateTimeOffset.UtcNow - kb.LastSyncedAt > TimeSpan.FromHours(kb.SyncIntervalHours));
                if (first && options.Value.SyncOnStartup) due = true;
                if (!due) continue;
                try
                {
                    if (await queue.HasPendingJobAsync(kb.Code, "folder-sync", ct)) continue;
                    var folder = SourceFolders.Resolve(kb.SourceFolder!, options.Value);
                    await queue.EnqueueAsync(kb.Code, "folder-sync", first ? "startup" : "schedule", [], folder, publish: true, SourceTrustLevel.AvantraInternal, "scheduler", ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Could not schedule sync for {Kb}", kb.Code); }
            }
            foreach (var kb in registry.All.Where(k => k.IsEnabled && !string.IsNullOrWhiteSpace(k.CrawlConfigJson) && k.SyncIntervalHours > 0))
            {
                // Website crawls are heavy: they start on schedule only after a first manual crawl (or when CrawlOnStartup is set).
                var due = kb.LastCrawledAt is null ? first && options.Value.CrawlOnStartup
                    : DateTimeOffset.UtcNow - kb.LastCrawledAt > TimeSpan.FromHours(kb.SyncIntervalHours);
                if (!due) continue;
                try
                {
                    if (await queue.HasPendingJobAsync(kb.Code, "crawl", ct)) continue;
                    var cfg = System.Text.Json.JsonSerializer.Deserialize<CrawlConfig>(kb.CrawlConfigJson!, CrawlJson.Options)!;
                    await queue.EnqueueAsync(kb.Code, "crawl", first ? "startup" : "schedule", [], null, publish: true, cfg.Trust, "scheduler", ct, cfg);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Could not schedule crawl for {Kb}", kb.Code); }
            }
            first = false;
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}

public sealed class KnowledgeReadiness
{
    private volatile bool _ready;
    public bool IsReady => _ready;
    public void MarkReady() => _ready = true;
}

public static class SourceFolders
{
    /// <summary>Resolves a knowledge base source folder and refuses anything outside the allowed root.</summary>
    public static string Resolve(string folder, IngestionOptions options)
    {
        var root = Path.GetFullPath(FileSystemCatalogueRepository.ResolvePath(options.AllowedSourceRoot)).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.IsPathRooted(folder) ? folder : FileSystemCatalogueRepository.ResolvePath(folder)).TrimEnd(Path.DirectorySeparatorChar);
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"Source folder must be inside the allowed root ({options.AllowedSourceRoot}).");
        return full;
    }
}

public static class CrawlJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
}
