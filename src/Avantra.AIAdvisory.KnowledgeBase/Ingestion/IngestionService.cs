using System.Security.Cryptography;
using Avantra.AIAdvisory.Application.Abstractions;
using Avantra.AIAdvisory.Application.Knowledge;
using Avantra.AIAdvisory.Application.Safety;
using Avantra.AIAdvisory.Domain.Catalogue;
using Avantra.AIAdvisory.Domain.Governance;
using Avantra.AIAdvisory.Domain.Knowledge;
using Avantra.AIAdvisory.KnowledgeBase.Parsing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Avantra.AIAdvisory.KnowledgeBase.Ingestion;

public sealed record IngestionRequest(
    string VendorCode,
    string FileName,
    string? ContentType,
    Stream Content,
    string? ExternalId = null,
    string? Title = null,
    string? SourceUrl = null,
    SourceTrustLevel Trust = SourceTrustLevel.Unverified,
    IReadOnlyList<string>? ProductCodes = null,
    bool PublishImmediately = false,
    bool IsDemonstrationData = false,
    DateOnly? RetrievedOn = null,
    bool Reprocess = false);

public enum IngestionOutcome { Created, NewVersion, Unchanged, Quarantined }

public sealed record IngestionResult(Guid DocumentId, string ExternalId, int Version, IngestionOutcome Outcome, DocumentStatus Status, int Chunks, string? Message);

/// <summary>
/// Acquire → parse → safety scan → chunk → classify → embed → persist → (publish) → index.
/// Content-hash based idempotency and versioning: re-ingesting an unchanged document is a no-op; a changed
/// document becomes a new version and the previous version is superseded and removed from the index.
/// </summary>
public sealed partial class IngestionService(
    DocumentParserRegistry parsers,
    IKnowledgeStore store,
    IKnowledgeIndex index,
    ICatalogueRepository catalogue,
    IKnowledgeBaseRegistry registry,
    IAuditLog audit,
    IUserContext user,
    IServiceProvider services,
    ILogger<IngestionService> logger)
{
    public async Task<IngestionResult> IngestAsync(IngestionRequest request, CancellationToken ct)
    {
        // A knowledge base needs no vendor pack; when one exists, chunks are also tagged with its products and capabilities.
        var pack = catalogue.GetPack(request.VendorCode);
        var kbCode = pack?.Vendor.Code ?? registry.Get(request.VendorCode)?.Code ?? throw new UnknownVendorException(request.VendorCode);
        using var buffer = new MemoryStream();
        await request.Content.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        // The hash covers the content AND the extraction pipeline version, so improving parsing/chunking re-processes
        // documents on the next sync instead of keeping chunks produced by the old pipeline.
        var hash = Convert.ToHexString(SHA256.HashData([.. bytes, .. System.Text.Encoding.UTF8.GetBytes("|pipeline:" + PipelineVersion)]));
        var externalId = request.ExternalId ?? Path.GetFileNameWithoutExtension(request.FileName).ToLowerInvariant();

        var existing = await store.FindByExternalIdAsync(kbCode, externalId, ct);
        if (existing is not null && existing.ContentHash == hash && !request.Reprocess)
            return new IngestionResult(existing.Id, externalId, existing.Version, IngestionOutcome.Unchanged, existing.Status, existing.Chunks.Count, "Content unchanged.");

        var parsed = await parsers.Resolve(request.FileName, request.ContentType).ParseAsync(new MemoryStream(bytes), request.FileName, ct);
        var meta = parsed.Metadata;
        var docProducts = (request.ProductCodes ?? Split(meta.GetValueOrDefault("products"))).ToList();
        var trust = request.Trust != SourceTrustLevel.Unverified ? request.Trust
            : Enum.TryParse<SourceTrustLevel>(meta.GetValueOrDefault("trust"), true, out var t) ? t : SourceTrustLevel.Unverified;

        var doc = new KnowledgeDocument
        {
            ExternalId = externalId,
            VendorCode = kbCode,
            Title = request.Title ?? parsed.Title ?? request.FileName,
            SourceUrl = request.SourceUrl ?? (meta.GetValueOrDefault("source_url") is { Length: > 0 } u ? u : null),
            ContentType = request.ContentType ?? Path.GetExtension(request.FileName),
            ContentHash = hash,
            Version = (existing?.Version ?? 0) + 1,
            Trust = trust,
            ProductCodes = docProducts,
            Tags = Split(meta.GetValueOrDefault("tags")).ToList(),
            RetrievedOn = request.RetrievedOn ?? (DateOnly.TryParse(meta.GetValueOrDefault("retrieved_on"), out var d) ? d : null),
            IngestedBy = user.UserId,
            IsDemonstrationData = request.IsDemonstrationData || string.Equals(meta.GetValueOrDefault("demonstration_data"), "true", StringComparison.OrdinalIgnoreCase),
        };

        var ordinal = 0;
        foreach (var (section, text) in Chunker.Chunk(parsed))
        {
            var (products, caps) = pack is null ? (docProducts.ToList(), new List<string>()) : KnowledgeClassifier.Classify(text, pack, docProducts);
            doc.Chunks.Add(new KnowledgeChunk { DocumentId = doc.Id, Ordinal = ordinal++, Section = section, Content = text, ProductCodes = products, CapabilityCodes = caps });
        }

        var fullText = string.Join("\n", doc.Chunks.Select(c => c.Content));
        if (ContentSafety.LooksLikePromptInjection(fullText, out var injection))
        {
            doc.Status = DocumentStatus.Quarantined;
            doc.StatusReason = $"Suspected prompt injection: '{injection}'";
        }
        else
        {
            doc.Status = request.PublishImmediately || trust >= SourceTrustLevel.VendorOfficial && user.IsInRole(Roles.KnowledgeAdmin)
                ? DocumentStatus.Published
                : DocumentStatus.PendingReview;
        }

        await EmbedAsync(doc, ct);

        if (existing is not null && doc.Status == DocumentStatus.Published)
        {
            existing.Status = DocumentStatus.Superseded;
            existing.StatusReason = $"Superseded by version {doc.Version}";
            await store.UpdateAsync(existing, ct);
            await index.RemoveAsync(existing.Id, ct);
        }

        await store.AddAsync(doc, ct);
        if (doc.Status == DocumentStatus.Published) await index.IndexAsync(doc, ct);

        await audit.WriteAsync(new AuditEntry
        {
            TenantId = user.TenantId, ActorId = user.UserId, Channel = user.Channel, Action = "knowledge.ingest",
            ResourceId = doc.Id.ToString(), Detail = $"{doc.VendorCode}/{doc.ExternalId} v{doc.Version} status={doc.Status} chunks={doc.Chunks.Count} trust={doc.Trust}",
        }, ct);

        var outcome = doc.Status == DocumentStatus.Quarantined ? IngestionOutcome.Quarantined
            : existing is null ? IngestionOutcome.Created : IngestionOutcome.NewVersion;
        logger.LogInformation("Ingested {Vendor}/{ExternalId} v{Version}: {Outcome} ({Chunks} chunks)", doc.VendorCode, externalId, doc.Version, outcome, doc.Chunks.Count);
        return new IngestionResult(doc.Id, externalId, doc.Version, outcome, doc.Status, doc.Chunks.Count, doc.StatusReason);
    }

    /// <summary>Bump when parsing, cleaning or chunking changes so existing documents are re-processed.</summary>
    public const string PipelineVersion = "2";

    /// <summary>Curator approval: publish a pending/quarantined document (and supersede older versions).</summary>
    public async Task<KnowledgeDocument?> PublishAsync(Guid documentId, CancellationToken ct)
    {
        if (!user.IsInRole(Roles.KnowledgeAdmin)) throw new UnauthorizedAccessException("Knowledge.Admin role required.");
        var doc = await store.GetAsync(documentId, ct);
        if (doc is null) return null;
        foreach (var older in (await store.ListAsync(doc.VendorCode, DocumentStatus.Published, ct)).Where(d => d.ExternalId == doc.ExternalId && d.Id != doc.Id))
        {
            older.Status = DocumentStatus.Superseded;
            await store.UpdateAsync(older, ct);
            await index.RemoveAsync(older.Id, ct);
        }
        doc.Status = DocumentStatus.Published;
        doc.StatusReason = $"Published by {user.UserId}";
        await store.UpdateAsync(doc, ct);
        await index.IndexAsync(doc, ct);
        await audit.WriteAsync(new AuditEntry { TenantId = user.TenantId, ActorId = user.UserId, Channel = user.Channel, Action = "knowledge.publish", ResourceId = doc.Id.ToString() }, ct);
        return doc;
    }

    private async Task EmbedAsync(KnowledgeDocument doc, CancellationToken ct)
    {
        var generator = services.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
        if (generator is null || doc.Chunks.Count == 0) return;
        try
        {
            foreach (var batch in doc.Chunks.Chunk(16))
            {
                var embeddings = await generator.GenerateAsync(batch.Select(c => $"{doc.Title}\n{c.Section}\n{c.Content}"), cancellationToken: ct);
                for (var i = 0; i < batch.Length; i++) batch[i].Embedding = embeddings[i].Vector.ToArray();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Embedding generation failed for {Doc}; document will be keyword-searchable only.", doc.ExternalId);
            foreach (var c in doc.Chunks) c.Embedding = null;
        }
    }

    private static IEnumerable<string> Split(string? csv) =>
        (csv ?? string.Empty).Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.Trim('"', '\''));
}
