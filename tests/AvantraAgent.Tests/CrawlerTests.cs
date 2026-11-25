using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using AvantraAgent.Knowledge;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AvantraAgent.Tests;

/// <summary>A small fake website: robots.txt, a sitemap index, pages (one disallowed, one excluded, one redirect off-site).</summary>
public sealed class FakeSite : HttpMessageHandler
{
    public int PageRequests;
    public string PlannerVersion = "Version one of the capacity planner page.";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        HttpResponseMessage Ok(string body, string type = "text/html") => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };
        if (request.RequestUri.Host != "docs.example.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        return Task.FromResult(path switch
        {
            "/robots.txt" => Ok("User-agent: *\nDisallow: /private/\nCrawl-delay: 0\n", "text/plain"),
            "/sitemap_index.xml" => Ok("""<?xml version="1.0"?><sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><sitemap><loc>https://docs.example.com/pages.xml</loc></sitemap></sitemapindex>""", "application/xml"),
            "/pages.xml" => Ok("""
                <?xml version="1.0"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                  <url><loc>https://docs.example.com/products/planner/</loc><lastmod>2020-01-01</lastmod></url>
                  <url><loc>https://docs.example.com/products/board/</loc><lastmod>2020-01-01</lastmod></url>
                  <url><loc>https://docs.example.com/private/secret/</loc></url>
                  <url><loc>https://docs.example.com/lp/campaign/</loc></url>
                  <url><loc>https://docs.example.com/products/moved/</loc></url>
                  <url><loc>https://docs.example.com/products/slow/</loc><lastmod>2020-01-01</lastmod></url>
                  <url><loc>https://docs.example.com/products/gated/</loc></url>
                  <url><loc>https://evil.example.net/products/x/</loc></url>
                </urlset>
                """, "application/xml"),
            "/products/planner/" => Count(Ok($"<html><head><title>Capacity Planner</title></head><body><nav>Menu Home About</nav><main><h1>Capacity Planner</h1><p>{PlannerVersion} The capacity planner balances demand and capacity by role.</p></main><footer>Copyright</footer></body></html>")),
            "/products/board/" => Count(Ok("<html><head><title>Kanban Board</title></head><body><main><h1>Kanban Board</h1><p>Kanban boards with WIP limits for agile teams of every size.</p></main></body></html>")),
            "/products/gated/" => Count(Ok("<html><head><title>Gated</title></head><body><main><p>Sign in to access this product help article. Gatedword.</p></main></body></html>")),
            "/products/slow/" => throw new TaskCanceledException("Simulated timeout", new TimeoutException()),
            "/products/moved/" => Count(new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("https://evil.example.net/landing") } }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
    }

    private HttpResponseMessage Count(HttpResponseMessage r) { Interlocked.Increment(ref PageRequests); return r; }
}

public sealed class CrawlFactory : AgentFactory
{
    public FakeSite Site { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddHttpClient("crawler").ConfigurePrimaryHttpMessageHandler(() => Site));
    }
}

public class CrawlerTests(CrawlFactory factory) : IClassFixture<CrawlFactory>
{
    [Fact]
    public async Task Crawl_follows_sitemaps_and_robots_and_skips_unchanged_pages()
    {
        var c = await factory.ReadyClientAsync();
        (await c.PostAsJsonAsync("/api/knowledge-bases", new { code = "docs", name = "Docs" })).EnsureSuccessStatusCode();
        var cfg = await c.PutAsJsonAsync("/api/knowledge-bases/docs/crawl-config", new
        {
            sitemaps = new[] { "https://docs.example.com/sitemap_index.xml" },
            exclude = new[] { "/lp/*" },
            skipIfContains = new[] { "Sign in to access this product help article" },
            maxPages = 100, minDelayMs = 250,
        });
        cfg.EnsureSuccessStatusCode();

        var first = await RunCrawl(c);
        Assert.Equal("PartiallySucceeded", first["state"]!.GetValue<string>()); // off-site redirect refused, slow page timed out
        Assert.Equal(5, first["items"]!.GetValue<int>());                       // planner, board, moved, slow, gated (private = robots, lp = excluded, evil = host)
        Assert.Contains("1 pages skipped because they need a sign-in", first["error"]!.GetValue<string>());
        Assert.Equal(2, first["created"]!.GetValue<int>());
        Assert.Equal(2, first["failed"]!.GetValue<int>());                      // a timeout fails the page, never the crawl

        var answer = await (await c.PostAsJsonAsync("/api/chat", new { message = "capacity planner demand by role", knowledgeBase = "docs" })).Content.ReadFromJsonAsync<JsonObject>();
        var text = answer!["answer"]!.GetValue<string>();
        Assert.Contains("balances demand and capacity", text);
        Assert.DoesNotContain("Menu Home About", text);                        // navigation stripped, main content kept
        var gated = await (await c.PostAsJsonAsync("/api/chat", new { message = "gatedword", knowledgeBase = "docs" })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Contains("couldn't find", gated!["answer"]!.GetValue<string>());     // sign-in wall page was not stored

        var requestsBefore = factory.Site.PageRequests;
        var second = await RunCrawl(c);
        Assert.Equal(2, second["unchanged"]!.GetValue<int>());                  // sitemap lastmod older than our copy: not fetched again
        Assert.Equal(requestsBefore + 2, factory.Site.PageRequests);             // only pages without lastmod (moved, gated) were requested

        var forced = await RunCrawl(c, force: true);
        Assert.Equal(2, forced["updated"]!.GetValue<int>());                    // force re-processes even identical pages
    }

    [Fact]
    public async Task Crawl_config_only_accepts_allowed_https_hosts()
    {
        var c = await factory.ReadyClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/knowledge-bases/planview/crawl-config",
            new { sitemaps = new[] { "https://evil.example.net/sitemap.xml" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/knowledge-bases/planview/crawl-config",
            new { sitemaps = new[] { "http://docs.example.com/sitemap.xml" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/knowledge-bases/planview/crawl-config",
            new { sitemaps = new[] { "https://docs.example.com/sitemap.xml" }, minDelayMs = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync($"/api/jobs/{Guid.NewGuid()}/cancel", null)).StatusCode);
    }

    private static async Task<JsonObject> RunCrawl(HttpClient c, bool force = false)
    {
        var res = await c.PostAsJsonAsync("/api/knowledge-bases/docs/crawl", new { force });
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        return await AgentFactory.WaitForJobAsync(c, (await res.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>());
    }
}

public class RobotsAndGlobTests
{
    [Fact]
    public void Robots_longest_match_wins_and_crawl_delay_is_read()
    {
        var r = Robots.Parse("User-agent: googlebot\nDisallow: /\n\nUser-agent: *\nDisallow: /wp-admin/\nAllow: /wp-admin/admin-ajax.php\nDisallow: /search/\nDisallow: /*?s=\nCrawl-delay: 5\n");
        Assert.Equal(5, r.CrawlDelaySeconds);
        Assert.True(r.IsAllowed("/products/portfolios/"));
        Assert.False(r.IsAllowed("/wp-admin/options.php"));
        Assert.True(r.IsAllowed("/wp-admin/admin-ajax.php"));
        Assert.False(r.IsAllowed("/search/kanban/"));
        Assert.False(r.IsAllowed("/page?s=x"));
    }

    [Theory]
    [InlineData("/lp/*", "/lp/campaign/", true)]
    [InlineData("/lp/*", "/products/lp/", false)]
    [InlineData("/company/careers*", "/company/careers/jobs/", true)]
    [InlineData("/Special:*", "/special:userlogin", true)]
    public void Glob_matches_from_path_start(string pattern, string path, bool expected) =>
        Assert.Equal(expected, WebsiteCrawler.Glob(pattern).IsMatch(path));

    [Fact]
    public void External_ids_are_stable_and_bounded()
    {
        var a = WebsiteCrawler.ExternalId(new Uri("https://www.planview.com/products-solutions/products/agileplace/"));
        Assert.Equal("web-www.planview.com-products-solutions-products-agileplace", a);
        var longId = WebsiteCrawler.ExternalId(new Uri("https://success.planview.com/" + new string('x', 400)));
        Assert.True(longId.Length <= 180);
    }
}
