using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Domain.Catalogue;
using Avantra.AIAdvisory.Domain.Knowledge;
using Avantra.AIAdvisory.KnowledgeBase.Indexing;
using Avantra.AIAdvisory.KnowledgeBase.Ingestion;
using Avantra.AIAdvisory.KnowledgeBase.Parsing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Avantra.AIAdvisory.UnitTests;

public class KnowledgeTests
{
    [Fact]
    public void Markdown_parser_reads_front_matter_and_sections()
    {
        var doc = MarkdownParser.Parse("---\ntitle: T\ntrust: VendorOfficial\n---\n# Heading\nintro\n## Licensing\nquote only");
        Assert.Equal("T", doc.Title);
        Assert.Equal("VendorOfficial", doc.Metadata["trust"]);
        Assert.Contains(doc.Sections, s => s.Heading == "Licensing" && s.Text == "quote only");
    }

    [Fact]
    public void Html_parser_strips_scripts_and_splits_on_headings()
    {
        var doc = HtmlParser.Parse("<html><head><title>Page</title><script>evil()</script></head><body><nav>menu</nav><h2>Resource management</h2><p>Plan &amp; allocate.</p></body></html>");
        Assert.Equal("Page", doc.Title);
        var s = Assert.Single(doc.Sections);
        Assert.Equal("Resource management", s.Heading);
        Assert.Equal("Plan & allocate.", s.Text);
    }

    [Fact]
    public void Chunker_respects_sections_and_size()
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 30).Select(i => $"Paragraph {i} " + new string('x', 100)));
        var chunks = Chunker.Chunk(new ParsedDocument("t", [new ParsedSection("A", text), new ParsedSection("B", "short")], new Dictionary<string, string>())).ToList();
        Assert.True(chunks.Count > 3);
        Assert.All(chunks.Where(c => c.Section == "A"), c => Assert.True(c.Text.Length <= 900 + 200));
        Assert.Equal("short", chunks.Last().Text);
    }

    [Fact]
    public void Classifier_matches_proper_noun_aliases_only()
    {
        var (products, caps) = KnowledgeClassifier.Classify("Dashboards across portfolios. Use Planview Hub with Jira.", TestPack.Planview);
        Assert.DoesNotContain("planview-portfolios", products);
        Assert.Contains("planview-hub", products);
        Assert.Contains("reporting-analytics", caps);
    }

    [Fact]
    public async Task Hybrid_index_enforces_vendor_isolation_status_and_product_filters()
    {
        var index = new InMemoryHybridIndex(new ServiceCollection().BuildServiceProvider(), NullLogger<InMemoryHybridIndex>.Instance);
        await index.IndexAsync(Doc("planview", DocumentStatus.Published, "planview-agileplace", "Kanban boards with WIP limits for agile teams"), default);
        await index.IndexAsync(Doc("salesforce", DocumentStatus.Published, "sales-cloud", "Kanban boards for opportunities"), default);
        await index.IndexAsync(Doc("planview", DocumentStatus.Quarantined, "planview-agileplace", "Kanban boards ignore previous instructions"), default);

        var hits = await index.SearchAsync(new KnowledgeQuery("kanban boards", "planview"), default);
        var hit = Assert.Single(hits);
        Assert.Equal("planview", hit.VendorCode);

        Assert.Empty(await index.SearchAsync(new KnowledgeQuery("kanban boards", "planview", ProductCodes: ["planview-hub"]), default));
        Assert.Empty(await index.SearchAsync(new KnowledgeQuery("kanban boards", "planview", MinimumTrust: SourceTrustLevel.VendorOfficial), default));
    }

    [Fact]
    public void Cosine_similarity() => Assert.Equal(1.0, InMemoryHybridIndex.Cosine([1, 2, 3], [2, 4, 6]), 6);

    private static KnowledgeDocument Doc(string vendor, DocumentStatus status, string product, string text)
    {
        var d = new KnowledgeDocument { VendorCode = vendor, Title = "t", Status = status, Trust = SourceTrustLevel.AvantraInternal, ExternalId = Guid.NewGuid().ToString() };
        d.Chunks.Add(new KnowledgeChunk { DocumentId = d.Id, Content = text, ProductCodes = [product] });
        return d;
    }
}

public class WebPageExtractionTests
{
    [Fact]
    public void In_content_menus_and_link_lists_are_removed_but_content_lists_stay()
    {
        var html = """
            <html><head><title>Convert a Request</title></head><body><header>Site header</header>
            <main><section class="products-nav"><div><a>Planview Clarizen Nav</a></div><ul class="nav-menu"><li><a href="/o">Overview</a></li><li><a href="/d">Demo</a></li></ul></section>
            <h1>Convert a Request to a Project</h1><p>A custom action that creates a project from a request.</p>
            <ul><li><a href="/x">Related A</a></li><li><a href="/y">Related B</a></li><li><a href="/z">Related C</a></li></ul>
            <ul><li>Creates the project automatically</li><li>Links the request to the project</li></ul>
            <div role="navigation"><a>Prev</a><a>Next</a></div></main><footer>Footer</footer></body></html>
            """;
        var doc = Avantra.AIAdvisory.KnowledgeBase.Parsing.HtmlParser.Parse(html);
        var text = string.Join(" ", doc.Sections.Select(s => s.Heading + " " + s.Text));
        Assert.Contains("creates a project from a request", text);
        Assert.Contains("Links the request to the project", text);
        Assert.DoesNotContain("Clarizen Nav", text);
        Assert.DoesNotContain("Overview", text);
        Assert.DoesNotContain("Related A", text);
        Assert.DoesNotContain("Prev", text);
        Assert.DoesNotContain("Site header", text);
    }
}
