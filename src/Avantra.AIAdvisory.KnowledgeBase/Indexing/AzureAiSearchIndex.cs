using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Domain.Catalogue;
using Avantra.AIAdvisory.Domain.Knowledge;
using Azure;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Avantra.AIAdvisory.KnowledgeBase.Indexing;

public sealed class AzureAiSearchOptions
{
    public const string Section = "Knowledge:AzureAiSearch";
    public string? Endpoint { get; set; }
    /// <summary>Optional. When empty, Managed Identity / DefaultAzureCredential is used (recommended).</summary>
    public string? ApiKey { get; set; }
    public string IndexName { get; set; } = "advisory-knowledge";
    public int VectorDimensions { get; set; } = 1536;
    public bool UseSemanticRanker { get; set; } = true;
}

/// <summary>
/// Production index: Azure AI Search hybrid query (BM25 + HNSW vector, RRF-fused) with optional semantic ranker.
/// Vendor isolation is enforced with a mandatory OData filter on <c>vendorCode</c> - one shared index, filtered
/// per query (an index-per-vendor model is an option if contractual isolation demands it).
/// </summary>
public sealed class AzureAiSearchIndex : IKnowledgeIndex
{
    private const string SemanticConfig = "advisory-semantic";
    private const string VectorProfile = "advisory-vector-profile";
    private readonly AzureAiSearchOptions _options;
    private readonly SearchIndexClient _indexClient;
    private readonly SearchClient _client;
    private readonly IServiceProvider _services;
    private readonly ILogger<AzureAiSearchIndex> _logger;
    private bool _ensured;

    public AzureAiSearchIndex(IOptions<AzureAiSearchOptions> options, IServiceProvider services, ILogger<AzureAiSearchIndex> logger)
    {
        _options = options.Value;
        _services = services;
        _logger = logger;
        var endpoint = new Uri(_options.Endpoint ?? throw new InvalidOperationException("Knowledge:AzureAiSearch:Endpoint is required."));
        _indexClient = string.IsNullOrWhiteSpace(_options.ApiKey)
            ? new SearchIndexClient(endpoint, new DefaultAzureCredential())
            : new SearchIndexClient(endpoint, new AzureKeyCredential(_options.ApiKey));
        _client = _indexClient.GetSearchClient(_options.IndexName);
    }

    public async Task EnsureIndexAsync(CancellationToken ct)
    {
        if (_ensured) return;
        var index = new SearchIndex(_options.IndexName)
        {
            Fields =
            {
                new SimpleField("id", SearchFieldDataType.String) { IsKey = true },
                new SimpleField("documentId", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("vendorCode", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                new SearchableField("title") { IsFilterable = true },
                new SearchableField("section"),
                new SearchableField("content") { AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
                new SimpleField("sourceUrl", SearchFieldDataType.String),
                new SimpleField("trust", SearchFieldDataType.Int32) { IsFilterable = true, IsSortable = true },
                new SimpleField("isDemonstrationData", SearchFieldDataType.Boolean) { IsFilterable = true },
                new SimpleField("productCodes", SearchFieldDataType.Collection(SearchFieldDataType.String)) { IsFilterable = true, IsFacetable = true },
                new SimpleField("capabilityCodes", SearchFieldDataType.Collection(SearchFieldDataType.String)) { IsFilterable = true, IsFacetable = true },
                new VectorSearchField("contentVector", _options.VectorDimensions, VectorProfile),
            },
            VectorSearch = new VectorSearch
            {
                Algorithms = { new HnswAlgorithmConfiguration("hnsw") },
                Profiles = { new VectorSearchProfile(VectorProfile, "hnsw") },
            },
            SemanticSearch = new SemanticSearch
            {
                Configurations =
                {
                    new SemanticConfiguration(SemanticConfig, new SemanticPrioritizedFields
                    {
                        TitleField = new SemanticField("title"),
                        ContentFields = { new SemanticField("content") },
                        KeywordsFields = { new SemanticField("section") },
                    }),
                },
            },
        };
        await _indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: ct);
        _ensured = true;
    }

    public async Task IndexAsync(KnowledgeDocument document, CancellationToken ct)
    {
        await EnsureIndexAsync(ct);
        var docs = document.Chunks.Select(c => new SearchDocument
        {
            ["id"] = c.Id.ToString(),
            ["documentId"] = document.Id.ToString(),
            ["vendorCode"] = document.VendorCode,
            ["title"] = document.Title,
            ["section"] = c.Section,
            ["content"] = c.Content,
            ["sourceUrl"] = document.SourceUrl,
            ["trust"] = (int)document.Trust,
            ["isDemonstrationData"] = document.IsDemonstrationData,
            ["productCodes"] = c.ProductCodes,
            ["capabilityCodes"] = c.CapabilityCodes,
            ["contentVector"] = c.Embedding,
        }).ToList();
        foreach (var batch in docs.Chunk(500))
            await _client.MergeOrUploadDocumentsAsync(batch, cancellationToken: ct);
    }

    public async Task RemoveAsync(Guid documentId, CancellationToken ct)
    {
        await EnsureIndexAsync(ct);
        var options = new SearchOptions { Filter = SearchFilter.Create($"documentId eq {documentId.ToString()}"), Size = 1000 };
        options.Select.Add("id");
        var response = await _client.SearchAsync<SearchDocument>("*", options, ct);
        var ids = new List<string>();
        await foreach (var r in response.Value.GetResultsAsync()) ids.Add((string)r.Document["id"]);
        if (ids.Count > 0) await _client.DeleteDocumentsAsync("id", ids, cancellationToken: ct);
    }

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(KnowledgeQuery query, CancellationToken ct)
    {
        await EnsureIndexAsync(ct);
        var filter = SearchFilter.Create($"vendorCode eq {query.VendorCode} and trust ge {(int)query.MinimumTrust}");
        if (query.ProductCodes is { Count: > 0 })
            filter += " and productCodes/any(p: search.in(p, '" + string.Join(",", query.ProductCodes.Select(p => p.Replace("'", ""))) + "', ','))";

        var options = new SearchOptions { Filter = filter, Size = query.Top };
        var generator = _services.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
        if (generator is not null)
        {
            var vector = await generator.GenerateVectorAsync(query.Text, cancellationToken: ct);
            options.VectorSearch = new VectorSearchOptions
            {
                Queries = { new VectorizedQuery(vector) { KNearestNeighborsCount = 50, Fields = { "contentVector" } } },
            };
        }
        if (_options.UseSemanticRanker)
        {
            options.QueryType = SearchQueryType.Semantic;
            options.SemanticSearch = new SemanticSearchOptions { SemanticConfigurationName = SemanticConfig };
        }

        var hits = new List<KnowledgeHit>();
        var response = await _client.SearchAsync<SearchDocument>(query.Text, options, ct);
        await foreach (var r in response.Value.GetResultsAsync())
        {
            var d = r.Document;
            hits.Add(new KnowledgeHit(
                Guid.Parse((string)d["id"]), Guid.Parse((string)d["documentId"]), (string)d["vendorCode"], (string)d["title"],
                d.TryGetValue("sourceUrl", out var u) ? u as string : null, d.TryGetValue("section", out var s) ? s as string : null,
                Trunc((string)d["content"]), r.SemanticSearch?.RerankerScore is { } rr ? rr / 4.0 : r.Score ?? 0,
                (SourceTrustLevel)Convert.ToInt32(d["trust"]), d.TryGetValue("isDemonstrationData", out var demo) && demo is true,
                ToList(d, "productCodes"), ToList(d, "capabilityCodes")));
        }
        _logger.LogDebug("Azure AI Search returned {Count} hits for vendor {Vendor}", hits.Count, query.VendorCode);
        return hits;
    }

    private static List<string> ToList(SearchDocument d, string key) =>
        d.TryGetValue(key, out var v) && v is IEnumerable<object> e ? e.Select(x => x.ToString()!).ToList() : [];

    private static string Trunc(string s) => s.Length <= 400 ? s : s[..397] + "...";
}
