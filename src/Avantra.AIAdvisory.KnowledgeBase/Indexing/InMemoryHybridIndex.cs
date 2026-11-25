using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Domain.Knowledge;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Avantra.AIAdvisory.KnowledgeBase.Indexing;

/// <summary>
/// In-process hybrid index for development, demos and tests: BM25 keyword ranking + optional cosine vector
/// ranking fused with Reciprocal Rank Fusion (the same fusion Azure AI Search uses for hybrid queries).
/// Production uses <see cref="AzureAiSearchIndex"/> behind the same <see cref="IKnowledgeIndex"/> port.
/// </summary>
public sealed partial class InMemoryHybridIndex(IServiceProvider services, ILogger<InMemoryHybridIndex> logger) : IKnowledgeIndex
{
    private const double K1 = 1.2, B = 0.75;
    private const int RrfK = 60;
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    [GeneratedRegex(@"[a-z0-9]+")]
    private static partial Regex Token();

    private static readonly HashSet<string> Stop = ["the", "and", "or", "a", "an", "of", "to", "in", "for", "with", "on", "is", "are", "be", "by", "as", "it", "that", "this", "we", "our", "from", "at", "can", "which", "what"];

    public Task IndexAsync(KnowledgeDocument document, CancellationToken ct)
    {
        foreach (var chunk in document.Chunks)
        {
            var tokens = Tokenize($"{document.Title} {chunk.Section} {chunk.Content}");
            var tf = tokens.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
            _entries[chunk.Id] = new Entry(document, chunk, tf, tokens.Count);
        }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(Guid documentId, CancellationToken ct)
    {
        foreach (var key in _entries.Where(e => e.Value.Document.Id == documentId).Select(e => e.Key).ToList())
            _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(KnowledgeQuery query, CancellationToken ct)
    {
        // Vendor isolation and trust filter are applied BEFORE ranking (security trimming, not post-filtering).
        var candidates = _entries.Values
            .Where(e => string.Equals(e.Document.VendorCode, query.VendorCode, StringComparison.OrdinalIgnoreCase))
            .Where(e => e.Document.Status == DocumentStatus.Published && e.Document.Trust >= query.MinimumTrust)
            .Where(e => query.ProductCodes is not { Count: > 0 } || e.Chunk.ProductCodes.Intersect(query.ProductCodes, StringComparer.OrdinalIgnoreCase).Any())
            .ToList();
        if (candidates.Count == 0) return [];

        var qTokens = Tokenize(query.Text).Distinct().ToList();
        var avgLen = candidates.Average(c => c.Length);
        var n = candidates.Count;
        var df = qTokens.ToDictionary(t => t, t => candidates.Count(c => c.Tf.ContainsKey(t)));

        var bm25 = candidates.Select(c => (Entry: c, Score: qTokens.Sum(t =>
        {
            if (!c.Tf.TryGetValue(t, out var f) || df[t] == 0) return 0;
            var idf = Math.Log(1 + (n - df[t] + 0.5) / (df[t] + 0.5));
            return idf * f * (K1 + 1) / (f + K1 * (1 - B + B * c.Length / avgLen));
        }))).Where(x => x.Score > 0).OrderByDescending(x => x.Score).ToList();

        var vector = await VectorRankAsync(query.Text, candidates, ct);

        var fused = new Dictionary<Guid, (Entry Entry, double Score)>();
        void Fuse(IEnumerable<(Entry Entry, double Score)> ranking)
        {
            var rank = 0;
            foreach (var (e, _) in ranking)
            {
                rank++;
                var add = 1.0 / (RrfK + rank);
                fused[e.Chunk.Id] = fused.TryGetValue(e.Chunk.Id, out var cur) ? (e, cur.Score + add) : (e, add);
            }
        }
        Fuse(bm25);
        if (vector is not null) Fuse(vector);

        // Normalise to 0..1 relative to a perfect rank-1 hit in every ranking; source trust only breaks near-ties.
        var maxPossible = (vector is null ? 1 : 2) * (1.0 / (RrfK + 1));
        return fused.Values
            .Select(x => (x.Entry, Score: x.Score / maxPossible * (0.99 + 0.0025 * (int)x.Entry.Document.Trust)))
            .OrderByDescending(x => x.Score)
            .Take(query.Top)
            .Select(x => new KnowledgeHit(x.Entry.Chunk.Id, x.Entry.Document.Id, x.Entry.Document.VendorCode, x.Entry.Document.Title,
                x.Entry.Document.SourceUrl, x.Entry.Chunk.Section, Snippet(x.Entry.Chunk.Content), Math.Round(x.Score, 4), x.Entry.Document.Trust,
                x.Entry.Document.IsDemonstrationData, x.Entry.Chunk.ProductCodes, x.Entry.Chunk.CapabilityCodes))
            .ToList();
    }

    private async Task<List<(Entry, double)>?> VectorRankAsync(string text, List<Entry> candidates, CancellationToken ct)
    {
        var generator = services.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
        if (generator is null || !candidates.Any(c => c.Chunk.Embedding is not null)) return null;
        try
        {
            var q = (await generator.GenerateVectorAsync(text, cancellationToken: ct)).ToArray();
            return candidates.Where(c => c.Chunk.Embedding is { } e && e.Length == q.Length)
                .Select(c => (c, Cosine(q, c.Chunk.Embedding!)))
                .OrderByDescending(x => x.Item2).Take(50).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Query embedding failed; using keyword ranking only.");
            return null;
        }
    }

    internal static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    internal static List<string> Tokenize(string text) =>
        Token().Matches(text.ToLowerInvariant()).Select(m => Stem(m.Value)).Where(t => t.Length > 1 && !Stop.Contains(t)).ToList();

    private static string Stem(string t) =>
        t.Length > 4 && t.EndsWith("ies") ? t[..^3] + "y"
        : t.Length > 4 && t.EndsWith("ing") ? t[..^3]
        : t.Length > 3 && t.EndsWith('s') && !t.EndsWith("ss") ? t[..^1]
        : t;

    private static string Snippet(string s) => s.Length <= 400 ? s : s[..397] + "...";

    private sealed record Entry(KnowledgeDocument Document, KnowledgeChunk Chunk, Dictionary<string, int> Tf, int Length);
}
