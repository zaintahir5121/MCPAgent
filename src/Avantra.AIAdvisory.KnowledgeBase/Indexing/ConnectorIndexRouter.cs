using System.Collections.Concurrent;
using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Domain.Knowledge;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Avantra.AIAdvisory.KnowledgeBase.Indexing;

public sealed class ConnectorIndexOptions
{
    public const string Section = "Search";
    /// <summary>InMemory (local/dev) or AzureAiSearch.</summary>
    public string Provider { get; set; } = "InMemory";
    public string? Endpoint { get; set; }
    /// <summary>Optional; when empty, managed identity (DefaultAzureCredential) is used.</summary>
    public string? ApiKey { get; set; }
    public int VectorDimensions { get; set; } = 1536;
    public bool UseSemanticRanker { get; set; } = true;
    /// <summary>Index name per connector (vendor code → index). Missing entries default to "{vendor}-index".</summary>
    public Dictionary<string, string> Indexes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string IndexNameFor(string vendorCode) =>
        Indexes.TryGetValue(vendorCode, out var name) && !string.IsNullOrWhiteSpace(name) ? name : $"{vendorCode.ToLowerInvariant()}-index";
}

/// <summary>
/// One separate knowledge index per connector (planview-index, salesforce-index, outsystems-index).
/// Every read and write is routed by vendor code, so connectors never share or mix an index.
/// </summary>
public sealed class ConnectorIndexRouter(IOptions<ConnectorIndexOptions> options, IServiceProvider services, ILoggerFactory loggers, IKnowledgeBaseRegistry? registry = null) : IKnowledgeIndex
{
    private readonly ConcurrentDictionary<string, IKnowledgeIndex> _indexes = new(StringComparer.OrdinalIgnoreCase);

    public bool IsPersistent => string.Equals(options.Value.Provider, "AzureAiSearch", StringComparison.OrdinalIgnoreCase);

    /// <summary>Index name: the knowledge base's own setting first, then configuration, then "{code}-index".</summary>
    public string IndexNameFor(string vendorCode) =>
        registry?.Get(vendorCode)?.IndexName is { Length: > 0 } n ? n : options.Value.IndexNameFor(vendorCode);

    public IKnowledgeIndex For(string vendorCode) => _indexes.GetOrAdd(vendorCode, v =>
    {
        var o = options.Value;
        if (!IsPersistent) return new InMemoryHybridIndex(services, loggers.CreateLogger<InMemoryHybridIndex>());
        return new AzureAiSearchIndex(Options.Create(new AzureAiSearchOptions
        {
            Endpoint = o.Endpoint, ApiKey = o.ApiKey, IndexName = IndexNameFor(v),
            VectorDimensions = o.VectorDimensions, UseSemanticRanker = o.UseSemanticRanker,
        }), services, loggers.CreateLogger<AzureAiSearchIndex>());
    });

    public Task IndexAsync(KnowledgeDocument document, CancellationToken ct) => For(document.VendorCode).IndexAsync(document, ct);

    /// <summary>Forget the cached index client (e.g. after a knowledge base is deleted).</summary>
    public void Evict(string vendorCode) => _indexes.TryRemove(vendorCode, out _);

    public async Task RemoveAsync(Guid documentId, CancellationToken ct)
    {
        foreach (var index in _indexes.Values) await index.RemoveAsync(documentId, ct);
    }

    public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(KnowledgeQuery query, CancellationToken ct) => For(query.VendorCode).SearchAsync(query, ct);
}
