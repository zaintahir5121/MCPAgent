using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Domain.Catalogue;
using Avantra.AIAdvisory.KnowledgeBase.Ingestion;
using Microsoft.Extensions.Options;

namespace AvantraAgent.Knowledge;

/// <summary>Website crawl settings for a knowledge base (stored as JSON on the knowledge base).</summary>
public sealed record CrawlConfig
{
    /// <summary>Sitemap URLs (sitemap indexes are followed). The main way to discover pages.</summary>
    public List<string> Sitemaps { get; init; } = [];
    /// <summary>Extra page URLs to ingest.</summary>
    public List<string> StartUrls { get; init; } = [];
    /// <summary>Path patterns to include, e.g. "/products-solutions/*". Empty = everything.</summary>
    public List<string> Include { get; init; } = [];
    /// <summary>Path patterns to skip, e.g. "/lp/*", "/de/*".</summary>
    public List<string> Exclude { get; init; } = [];
    /// <summary>Maximum pages fetched per crawl (pages skipped because the sitemap says they are unchanged do not count).</summary>
    public int MaxPages { get; init; } = 1000;
    /// <summary>Minimum pause between requests to the same host; robots.txt Crawl-delay wins when larger.</summary>
    public int MinDelayMs { get; init; } = 1000;
    public SourceTrustLevel Trust { get; init; } = SourceTrustLevel.VendorOfficial;
}

public sealed record CrawlProgress(int Discovered, int Fetched, int Created, int Updated, int Unchanged, int Skipped, int Quarantined, int Failed);

/// <summary>
/// Polite website crawler: robots.txt rules and Crawl-delay, allowed https hosts only, same-host redirects only,
/// sitemap lastmod to skip unchanged pages, size limits, cancellable. Each page's main content becomes a document.
/// </summary>
public sealed partial class WebsiteCrawler(IHttpClientFactory httpFactory, IServiceScopeFactory scopes, IOptions<IngestionOptions> options, ILogger<WebsiteCrawler> logger)
{
    public const string UserAgent = "AvantraAgent-KnowledgeCrawler/1.0";
    private const long MaxPageBytes = 10L * 1024 * 1024;
    private const long MaxSitemapBytes = 60L * 1024 * 1024;

    public sealed record PageResult(string Url, string Outcome, string? Message);

    public async Task<(CrawlProgress Progress, List<PageResult> Results)> CrawlAsync(
        string kb, CrawlConfig cfg, int? maxPagesOverride, bool force, Func<CrawlProgress, Task> report, Func<bool> cancelled, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("crawler");
        var robots = new Dictionary<string, Robots>(StringComparer.OrdinalIgnoreCase);
        var lastRequest = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var include = cfg.Include.Select(Glob).ToList();
        var exclude = cfg.Exclude.Select(Glob).ToList();
        var maxPages = Math.Clamp(maxPagesOverride ?? cfg.MaxPages, 1, 50_000);

        async Task<Robots> RobotsFor(Uri u)
        {
            if (robots.TryGetValue(u.Host, out var r)) return r;
            try
            {
                var (status, body, _) = await GetAsync(http, new Uri($"https://{u.Host}/robots.txt"), 512 * 1024, Politeness, ct);
                r = status == HttpStatusCode.OK ? Robots.Parse(body) : Robots.AllowAll;
            }
            catch (HttpRequestException) { r = Robots.AllowAll; }
            return robots[u.Host] = r;
        }

        async Task Politeness(Uri u)
        {
            var r = robots.GetValueOrDefault(u.Host) ?? Robots.AllowAll;
            var delay = TimeSpan.FromMilliseconds(Math.Max(cfg.MinDelayMs, (r.CrawlDelaySeconds ?? 0) * 1000));
            if (lastRequest.TryGetValue(u.Host, out var last))
            {
                var wait = last + delay - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            }
            lastRequest[u.Host] = DateTime.UtcNow;
        }

        // 1. Discover pages from sitemaps (+ start URLs)
        var pages = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
        foreach (var sm in cfg.Sitemaps)
            if (Allowed(sm, out var smUri)) { await RobotsFor(smUri!); await ReadSitemapAsync(http, smUri!, pages, depth: 0, Politeness, ct); }
        foreach (var u in cfg.StartUrls) if (Allowed(u, out _)) pages.TryAdd(u, null);

        var candidates = new List<(Uri Url, DateTimeOffset? LastMod)>();
        foreach (var (url, lastmod) in pages)
        {
            if (!Allowed(url, out var uri)) continue;
            var path = uri!.PathAndQuery;
            if (include.Count > 0 && !include.Any(r => r.IsMatch(path))) continue;
            if (exclude.Any(r => r.IsMatch(path))) continue;
            if (!(await RobotsFor(uri)).IsAllowed(path)) continue;
            candidates.Add((uri, lastmod));
        }
        logger.LogInformation("Crawl of {Kb}: {Discovered} pages discovered, {Candidates} eligible, max {Max} fetches", kb, pages.Count, candidates.Count, maxPages);

        // 2. Fetch and ingest
        int fetched = 0, created = 0, updated = 0, unchanged = 0, skipped = 0, quarantined = 0, failed = 0;
        var results = new List<PageResult>();
        void Note(string url, string outcome, string? msg = null) { if (results.Count < 3000) results.Add(new PageResult(url, outcome, msg)); }
        CrawlProgress Progress() => new(candidates.Count, fetched, created, updated, unchanged, skipped, quarantined, failed);

        using var scope = scopes.CreateScope();
        var ingestion = scope.ServiceProvider.GetRequiredService<IngestionService>();
        var store = scope.ServiceProvider.GetRequiredService<IKnowledgeStore>();
        var processed = 0;
        foreach (var (uri, lastmod) in candidates)
        {
            if (cancelled() || ct.IsCancellationRequested) break;
            if (fetched >= maxPages) { skipped += candidates.Count - processed; break; }
            processed++;
            var externalId = ExternalId(uri);

            // Sitemap says unchanged since we last ingested it → no request needed.
            if (!force && lastmod is { } lm && await store.FindByExternalIdAsync(kb, externalId, ct) is { } existing && lm <= existing.IngestedAt)
            {
                unchanged++;
                continue;
            }

            try
            {
                fetched++;
                var (status, body, final) = await GetAsync(http, uri, MaxPageBytes, Politeness, ct);
                if (status != HttpStatusCode.OK || body.Length == 0) { failed++; Note(uri.ToString(), "Failed", $"HTTP {(int)status}"); continue; }
                var main = Avantra.AIAdvisory.KnowledgeBase.Parsing.HtmlParser.MainContent(body);
                var title = TitleRegex().Match(body) is { Success: true } t ? t.Value : "";
                var html = $"<html><head>{title}</head><body>{main}</body></html>";
                await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));
                var r = await ingestion.IngestAsync(new IngestionRequest(kb, Slug(uri) + ".html", "text/html", stream,
                    ExternalId: externalId, SourceUrl: final.ToString(), Trust: cfg.Trust, PublishImmediately: true), ct);
                switch (r.Outcome)
                {
                    case IngestionOutcome.Created: created++; break;
                    case IngestionOutcome.NewVersion: updated++; break;
                    case IngestionOutcome.Unchanged: unchanged++; break;
                    case IngestionOutcome.Quarantined: quarantined++; Note(uri.ToString(), "Quarantined", r.Message); break;
                }
                if (r.Chunks == 0 && r.Outcome == IngestionOutcome.Created) Note(uri.ToString(), "Empty", "No readable content");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                Note(uri.ToString(), "Failed", ex.Message);
                logger.LogDebug(ex, "Crawl failed for {Url}", uri);
            }
            if (processed % 20 == 0) await report(Progress());
        }
        return (Progress(), results);
    }

    private bool Allowed(string url, out Uri? uri)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host;
        return options.Value.AllowedUrlHosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
    }

    private async Task ReadSitemapAsync(HttpClient http, Uri sitemap, Dictionary<string, DateTimeOffset?> pages, int depth, Func<Uri, Task> politeness, CancellationToken ct)
    {
        if (depth > 2) return;
        try
        {
            var (status, body, _) = await GetAsync(http, sitemap, MaxSitemapBytes, politeness, ct);
            if (status != HttpStatusCode.OK) { logger.LogWarning("Sitemap {Url} returned {Status}", sitemap, (int)status); return; }
            using var reader = XmlReader.Create(new StringReader(body), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var doc = XDocument.Load(reader);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            if (doc.Root?.Name.LocalName == "sitemapindex")
            {
                foreach (var loc in doc.Root.Elements(ns + "sitemap").Select(e => (string?)e.Element(ns + "loc")))
                    if (loc is not null && Allowed(loc.Trim(), out var child)) await ReadSitemapAsync(http, child!, pages, depth + 1, politeness, ct);
                return;
            }
            foreach (var u in doc.Root?.Elements(ns + "url") ?? [])
            {
                var loc = ((string?)u.Element(ns + "loc"))?.Trim();
                if (string.IsNullOrEmpty(loc)) continue;
                DateTimeOffset? lastmod = DateTimeOffset.TryParse((string?)u.Element(ns + "lastmod"), out var lm) ? lm : null;
                pages.TryAdd(loc, lastmod);
            }
        }
        catch (Exception ex) when (ex is XmlException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read sitemap {Url}", sitemap);
        }
    }

    /// <summary>GET with manual redirects (same allowed host, https, max 3) and a size cap.</summary>
    private async Task<(HttpStatusCode Status, string Body, Uri Final)> GetAsync(HttpClient http, Uri uri, long maxBytes, Func<Uri, Task> politeness, CancellationToken ct)
    {
        for (var hop = 0; hop < 4; hop++)
        {
            await politeness(uri);
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,text/plain;q=0.8");
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)res.StatusCode is >= 300 and < 400 && res.Headers.Location is { } loc)
            {
                var next = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                if (next.Scheme != Uri.UriSchemeHttps || !Allowed(next.ToString(), out _)) return (res.StatusCode, "", uri);
                uri = next;
                continue;
            }
            if (!res.IsSuccessStatusCode) return (res.StatusCode, "", uri);
            if (res.Content.Headers.ContentLength > maxBytes) return (HttpStatusCode.RequestEntityTooLarge, "", uri);
            await using var s = await res.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await s.ReadAsync(buffer, ct)) > 0)
            {
                if (ms.Length + read > maxBytes) return (HttpStatusCode.RequestEntityTooLarge, "", uri);
                ms.Write(buffer, 0, read);
            }
            return (res.StatusCode, Encoding.UTF8.GetString(ms.ToArray()), uri);
        }
        return (HttpStatusCode.LoopDetected, "", uri);
    }

    /// <summary>Stable document id per page URL, so re-crawls create versions instead of duplicates.</summary>
    public static string ExternalId(Uri uri)
    {
        var id = "web-" + uri.Host + "-" + Slug(uri);
        if (id.Length <= 180) return id;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.ToString())))[..16].ToLowerInvariant();
        return id[..160] + "-" + hash;
    }

    private static string Slug(Uri uri)
    {
        var s = Regex.Replace(Uri.UnescapeDataString(uri.AbsolutePath + uri.Query).ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return s.Length == 0 ? "home" : s;
    }

    /// <summary>Path pattern: "*" matches anything; patterns match from the start of the path.</summary>
    public static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*"), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"<title[^>]*>.*?</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();
}

/// <summary>Minimal robots.txt support: the "*" group's Allow/Disallow (longest match wins, * and $ wildcards) and Crawl-delay.</summary>
public sealed class Robots
{
    public static readonly Robots AllowAll = new([], null);
    private readonly List<(bool Allow, Regex Rule, int Length)> _rules;
    public double? CrawlDelaySeconds { get; }

    private Robots(List<(bool, Regex, int)> rules, double? delay) { _rules = rules; CrawlDelaySeconds = delay; }

    public static Robots Parse(string text)
    {
        var rules = new List<(bool, Regex, int)>();
        double? delay = null;
        bool inStar = false, lastWasAgent = false;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim().ToLowerInvariant();
            var value = line[(idx + 1)..].Trim();
            if (key == "user-agent")
            {
                var star = value == "*";
                inStar = lastWasAgent ? inStar || star : star;
                lastWasAgent = true;
                continue;
            }
            lastWasAgent = false;
            if (!inStar) continue;
            if (key is "allow" or "disallow" && value.Length > 0)
            {
                var regex = "^" + Regex.Escape(value).Replace("\\*", ".*").Replace("\\$", "$");
                if (regex.EndsWith("\\$", StringComparison.Ordinal)) regex = regex[..^2] + "$";
                rules.Add((key == "allow", new Regex(regex, RegexOptions.CultureInvariant), value.Length));
            }
            else if (key == "crawl-delay" && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                delay = d;
        }
        return new Robots(rules, delay);
    }

    public bool IsAllowed(string pathAndQuery)
    {
        var match = _rules.Where(r => r.Rule.IsMatch(pathAndQuery)).OrderByDescending(r => r.Length).ThenByDescending(r => r.Allow).FirstOrDefault();
        return match.Rule is null || match.Allow;
    }
}
