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

public sealed record IngestionWorkItem(Guid JobId, string KnowledgeBase, string Kind, IReadOnlyList<StagedFile> Files, string? Folder, bool Publish, SourceTrustLevel Trust);

public sealed record IngestedItem(string Name, string Outcome, string Status, int Chunks, string? Message);

/// <summary>Accepts ingestion work, records it as a job in the database and hands it to the background worker.</summary>
public sealed class IngestionQueue(IServiceScopeFactory scopes, IOptions<IngestionOptions> options)
{
    private readonly Channel<IngestionWorkItem> _channel = Channel.CreateBounded<IngestionWorkItem>(
        new BoundedChannelOptions(Math.Max(10, options.Value.QueueCapacity)) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    public ChannelReader<IngestionWorkItem> Reader => _channel.Reader;

    public async Task<IngestionJobRecord> EnqueueAsync(string kb, string kind, string trigger, IReadOnlyList<StagedFile> files, string? folder,
        bool publish, SourceTrustLevel trust, string requestedBy, CancellationToken ct)
    {
        var job = new IngestionJobRecord { KnowledgeBaseCode = kb, Kind = kind, Trigger = trigger, Items = files.Count, RequestedBy = requestedBy };
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AdvisoryDbContext>();
            db.IngestionJobs.Add(job);
            await db.SaveChangesAsync(ct);
        }
        if (!_channel.Writer.TryWrite(new IngestionWorkItem(job.Id, kb, kind, files, folder, publish, trust)))
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

/// <summary>Processes ingestion jobs one at a time (parse → screen → chunk → embed → store → index).</summary>
public sealed class IngestionWorker(
    IngestionQueue queue, IServiceScopeFactory scopes, EfKnowledgeBaseRegistry registry, ILogger<IngestionWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Jobs that were queued/running when the service stopped do not resume (their staged input may be gone).
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

        await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try { await ProcessAsync(item, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ingestion job {JobId} failed", item.JobId);
                await queue.UpdateAsync(item.JobId, j => { j.State = IngestionJobState.Failed; j.Error = ex.Message; j.FinishedAt = DateTimeOffset.UtcNow; }, CancellationToken.None);
                foreach (var f in item.Files) IngestionQueue.TryDelete(f.Path);
            }
        }
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
            j.State = failed == 0 ? IngestionJobState.Succeeded : failed < results.Count ? IngestionJobState.PartiallySucceeded : IngestionJobState.Failed;
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
