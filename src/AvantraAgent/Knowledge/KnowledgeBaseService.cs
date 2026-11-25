using System.Net;
using System.Text.Json;
using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Domain.Catalogue;
using Avantra.AIAdvisory.Domain.Knowledge;
using Avantra.AIAdvisory.Infrastructure.Persistence;
using Avantra.AIAdvisory.KnowledgeBase.Indexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AvantraAgent.Knowledge;

public sealed record CreateKnowledgeBaseRequest(string Code, string Name, string? Description = null, string? IndexName = null, string? SourceFolder = null, double SyncIntervalHours = 0);
public sealed record UpdateKnowledgeBaseRequest(string? Name = null, string? Description = null, string? SourceFolder = null, double? SyncIntervalHours = null, bool? IsEnabled = null);
public sealed record UrlIngestionRequest(string Url, bool? Publish = null, SourceTrustLevel? Trust = null);

public sealed record KnowledgeBaseView(
    string Code, string Name, string? Description, string IndexName, string SearchProvider, bool AdvisoryEnabled, bool IsEnabled,
    string? SourceFolder, double SyncIntervalHours, DateTimeOffset? LastSyncedAt, int PublishedDocuments, int PendingReview, int Quarantined,
    bool HasCrawl = false, DateTimeOffset? LastCrawledAt = null);

/// <param name="Force">Re-fetch every page even if the sitemap says it is unchanged (e.g. after improving extraction).</param>
public sealed record StartCrawlRequest(int? MaxPages = null, bool Force = false);

public sealed record JobView(
    Guid Id, string KnowledgeBase, string Kind, string Trigger, string State, int Items, int Created, int Updated, int Unchanged,
    int PendingReview, int Quarantined, int Failed, string? Error, DateTimeOffset QueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    IReadOnlyList<IngestedItem>? Details)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static JobView From(IngestionJobRecord j, bool details = true) => new(j.Id, j.KnowledgeBaseCode, j.Kind, j.Trigger, j.State.ToString(), j.Items,
        j.Created, j.Updated, j.Unchanged, j.PendingReview, j.Quarantined, j.Failed, j.Error, j.QueuedAt, j.StartedAt, j.FinishedAt,
        details && j.DetailsJson is not null ? JsonSerializer.Deserialize<List<IngestedItem>>(j.DetailsJson, Json) : null);
}

/// <summary>Everything needed to add more and more knowledge: create knowledge bases and feed them documents.</summary>
public sealed class KnowledgeBaseService(
    EfKnowledgeBaseRegistry registry, ConnectorIndexRouter router, IngestionQueue queue, IKnowledgeStore store,
    ICatalogueRepository catalogue, AdvisoryDbContext db, IHttpClientFactory httpFactory, IUserContext user,
    IOptions<IngestionOptions> options, IOptions<ConnectorIndexOptions> search, ILogger<KnowledgeBaseService> logger)
{
    private readonly IngestionOptions _o = options.Value;

    public KnowledgeBaseDefinition Require(string code) =>
        registry.Get(code) ?? throw new KeyNotFoundException($"Knowledge base '{code}' does not exist.");

    public async Task<IReadOnlyList<KnowledgeBaseView>> ListAsync(CancellationToken ct)
    {
        var counts = await db.Documents.AsNoTracking()
            .GroupBy(d => new { d.VendorCode, d.Status })
            .Select(g => new { g.Key.VendorCode, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);
        return registry.All.Select(k => View(k, counts.Where(c => c.VendorCode == k.Code).ToDictionary(c => c.Status, c => c.Count))).ToList();
    }

    public async Task<KnowledgeBaseView> GetAsync(string code, CancellationToken ct) =>
        (await ListAsync(ct)).FirstOrDefault(k => k.Code == code) ?? throw new KeyNotFoundException($"Knowledge base '{code}' does not exist.");

    private KnowledgeBaseView View(KnowledgeBaseDefinition k, Dictionary<DocumentStatus, int> c) => new(
        k.Code, k.Name, k.Description, router.IndexNameFor(k.Code), search.Value.Provider, catalogue.GetPack(k.Code) is not null, k.IsEnabled,
        k.SourceFolder, k.SyncIntervalHours, k.LastSyncedAt,
        c.GetValueOrDefault(DocumentStatus.Published), c.GetValueOrDefault(DocumentStatus.PendingReview), c.GetValueOrDefault(DocumentStatus.Quarantined),
        !string.IsNullOrWhiteSpace(k.CrawlConfigJson), k.LastCrawledAt);

    public async Task<KnowledgeBaseView> CreateAsync(CreateKnowledgeBaseRequest r, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(r.SourceFolder)) SourceFolders.Resolve(r.SourceFolder, _o);
        var kb = await registry.CreateAsync(new KnowledgeBaseDefinition
        {
            Code = r.Code, Name = r.Name?.Trim() ?? "", Description = r.Description?.Trim(), IndexName = r.IndexName ?? "",
            SourceFolder = string.IsNullOrWhiteSpace(r.SourceFolder) ? null : r.SourceFolder.Trim(), SyncIntervalHours = r.SyncIntervalHours,
        }, ct);
        logger.LogInformation("Knowledge base {Code} created (index {Index}) by {User}", kb.Code, kb.IndexName, user.UserId);
        return await GetAsync(kb.Code, ct);
    }

    public async Task<KnowledgeBaseView> UpdateAsync(string code, UpdateKnowledgeBaseRequest r, CancellationToken ct)
    {
        Require(code);
        if (!string.IsNullOrWhiteSpace(r.SourceFolder)) SourceFolders.Resolve(r.SourceFolder, _o);
        await registry.UpdateAsync(code, k =>
        {
            if (!string.IsNullOrWhiteSpace(r.Name)) k.Name = r.Name.Trim();
            if (r.Description is not null) k.Description = r.Description.Trim();
            if (r.SourceFolder is not null) k.SourceFolder = string.IsNullOrWhiteSpace(r.SourceFolder) ? null : r.SourceFolder.Trim();
            if (r.SyncIntervalHours is not null) k.SyncIntervalHours = r.SyncIntervalHours.Value;
            if (r.IsEnabled is not null) k.IsEnabled = r.IsEnabled.Value;
        }, ct);
        return await GetAsync(code, ct);
    }

    public async Task DeleteAsync(string code, CancellationToken ct)
    {
        Require(code);
        var ids = await registry.DeleteAsync(code, ct);
        foreach (var id in ids) await router.RemoveAsync(id, ct);
        router.Evict(code);
        logger.LogWarning("Knowledge base {Code} deleted with {Docs} documents by {User}", code, ids.Count, user.UserId);
    }

    public async Task<IngestionJobRecord> UploadAsync(string code, IReadOnlyList<IFormFile> files, bool? publish, SourceTrustLevel? trust, string? sourceUrl, CancellationToken ct)
    {
        Require(code);
        if (files.Count == 0) throw new ArgumentException("Attach at least one file.");
        if (files.Count > _o.MaxFilesPerUpload) throw new ArgumentException($"At most {_o.MaxFilesPerUpload} files per upload.");
        foreach (var f in files)
        {
            if (f.Length == 0 || f.Length > _o.MaxFileSizeMb * 1024L * 1024L) throw new ArgumentException($"'{f.FileName}' must be between 1 byte and {_o.MaxFileSizeMb} MB.");
            if (!IngestionOptions.SupportedExtensions.Contains(Path.GetExtension(f.FileName).ToLowerInvariant()))
                throw new NotSupportedException($"'{f.FileName}': supported types are {string.Join(", ", IngestionOptions.SupportedExtensions)}.");
        }
        var staged = new List<StagedFile>();
        foreach (var f in files)
        {
            var path = StagingPath(Path.GetExtension(f.FileName));
            await using (var s = File.Create(path)) await f.CopyToAsync(s, ct);
            staged.Add(new StagedFile(Path.GetFileName(f.FileName), path, f.ContentType, sourceUrl));
        }
        return await queue.EnqueueAsync(code, "upload", "api", staged, null, publish ?? _o.PublishUploadsByDefault, trust ?? SourceTrustLevel.Unverified, user.UserId, ct);
    }

    public async Task<IngestionJobRecord> IngestUrlAsync(string code, UrlIngestionRequest r, CancellationToken ct)
    {
        Require(code);
        if (!Uri.TryCreate(r.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Provide an absolute https:// URL.");
        if (!_o.AllowedUrlHosts.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(_o.AllowedUrlHosts.Count == 0
                ? "URL ingestion is disabled. Add trusted hosts to Ingestion:AllowedUrlHosts."
                : $"Host '{uri.Host}' is not in Ingestion:AllowedUrlHosts.");

        using var http = httpFactory.CreateClient("ingestion");
        using var res = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectKeepVerb or HttpStatusCode.RedirectMethod)
            throw new ArgumentException("The URL redirects; submit the final URL instead.");
        if (!res.IsSuccessStatusCode) throw new ArgumentException($"Download failed with HTTP {(int)res.StatusCode}.");
        var media = res.Content.Headers.ContentType?.MediaType ?? "";
        var ext = media switch
        {
            "application/pdf" => ".pdf",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "text/html" => ".html",
            "text/markdown" => ".md",
            "text/plain" => Path.GetExtension(uri.AbsolutePath) is ".md" ? ".md" : ".txt",
            _ => throw new NotSupportedException($"Unsupported content type '{media}'."),
        };
        var max = _o.MaxFileSizeMb * 1024L * 1024L;
        if (res.Content.Headers.ContentLength > max) throw new ArgumentException($"Document is larger than {_o.MaxFileSizeMb} MB.");

        var path = StagingPath(ext);
        await using (var input = await res.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(path))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > max) { output.Close(); IngestionQueue.TryDelete(path); throw new ArgumentException($"Document is larger than {_o.MaxFileSizeMb} MB."); }
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        var name = Path.GetFileName(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(name) || !Path.HasExtension(name)) name = (string.IsNullOrWhiteSpace(name) ? uri.Host : name) + ext;
        return await queue.EnqueueAsync(code, "url", "api", [new StagedFile(name, path, media, uri.ToString())], null,
            r.Publish ?? _o.PublishUploadsByDefault, r.Trust ?? SourceTrustLevel.ThirdParty, user.UserId, ct);
    }

    public async Task<IngestionJobRecord> SyncAsync(string code, CancellationToken ct)
    {
        var kb = Require(code);
        if (string.IsNullOrWhiteSpace(kb.SourceFolder)) throw new ArgumentException($"Knowledge base '{code}' has no source folder. Set one, or upload documents instead.");
        if (await queue.HasPendingJobAsync(code, "folder-sync", ct)) throw new InvalidOperationException("A sync for this knowledge base is already queued or running.");
        return await queue.EnqueueAsync(code, "folder-sync", "api", [], SourceFolders.Resolve(kb.SourceFolder, _o), publish: true, SourceTrustLevel.AvantraInternal, user.UserId, ct);
    }

    public CrawlConfig? GetCrawlConfig(string code)
    {
        var kb = Require(code);
        return string.IsNullOrWhiteSpace(kb.CrawlConfigJson) ? null : JsonSerializer.Deserialize<CrawlConfig>(kb.CrawlConfigJson, CrawlJson.Options);
    }

    /// <summary>Set (or clear with null) the website crawl of a knowledge base. All URLs must be https on Ingestion:AllowedUrlHosts.</summary>
    public async Task<CrawlConfig?> SetCrawlConfigAsync(string code, CrawlConfig? cfg, CancellationToken ct)
    {
        Require(code);
        if (cfg is not null)
        {
            if (cfg.Sitemaps.Count + cfg.StartUrls.Count == 0) throw new ArgumentException("Provide at least one sitemap or start URL.");
            foreach (var u in cfg.Sitemaps.Concat(cfg.StartUrls))
            {
                if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException($"'{u}' is not an https URL.");
                if (!_o.AllowedUrlHosts.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"Host '{uri.Host}' is not in Ingestion:AllowedUrlHosts.");
            }
            if (cfg.MaxPages is < 1 or > 50_000) throw new ArgumentException("MaxPages must be 1-50000.");
            if (cfg.MinDelayMs < 250) throw new ArgumentException("MinDelayMs must be at least 250 ms (be polite to the website).");
        }
        await registry.UpdateAsync(code, k => k.CrawlConfigJson = cfg is null ? null : JsonSerializer.Serialize(cfg, CrawlJson.Options), ct);
        return cfg;
    }

    public async Task<IngestionJobRecord> StartCrawlAsync(string code, int? maxPages, CancellationToken ct, bool force = false)
    {
        var cfg = GetCrawlConfig(code) ?? throw new ArgumentException($"Knowledge base '{code}' has no website crawl configured (PUT .../crawl-config first).");
        if (await queue.HasPendingJobAsync(code, "crawl", ct)) throw new InvalidOperationException("A crawl for this knowledge base is already queued or running.");
        return await queue.EnqueueAsync(code, "crawl", "api", [], null, publish: true, cfg.Trust, user.UserId, ct, cfg, maxPages, force);
    }

    public async Task<bool> CancelJobAsync(Guid id, CancellationToken ct)
    {
        var job = await db.IngestionJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return false;
        if (job.State is not (IngestionJobState.Queued or IngestionJobState.Running)) throw new InvalidOperationException($"Job is already {job.State}.");
        queue.RequestCancel(id);
        return true;
    }

    public async Task<IReadOnlyList<JobView>> JobsAsync(string? code, int take, CancellationToken ct)
    {
        var q = db.IngestionJobs.AsNoTracking();
        if (code is not null) q = q.Where(j => j.KnowledgeBaseCode == code);
        var jobs = await q.ToListAsync(ct);
        return jobs.OrderByDescending(j => j.QueuedAt).Take(Math.Clamp(take, 1, 100)).Select(j => JobView.From(j, details: false)).ToList();
    }

    public async Task<JobView?> JobAsync(Guid id, CancellationToken ct) =>
        await db.IngestionJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct) is { } j ? JobView.From(j) : null;

    public async Task<bool> DeleteDocumentAsync(string code, Guid id, CancellationToken ct)
    {
        Require(code);
        var doc = await store.GetAsync(id, ct);
        if (doc is null || doc.VendorCode != code) return false;
        await router.RemoveAsync(id, ct);
        return await store.DeleteAsync(id, ct);
    }

    private string StagingPath(string extension)
    {
        var dir = Path.IsPathRooted(_o.StagingPath) ? _o.StagingPath : Path.Combine(AppContext.BaseDirectory, _o.StagingPath);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{Guid.NewGuid():N}{extension}");
    }
}
