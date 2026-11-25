using Avantra.AIAdvisory.Domain.Catalogue;

namespace Avantra.AIAdvisory.Domain.Knowledge;

public enum DocumentStatus
{
    /// <summary>Ingested but awaiting curator review; never retrievable by advisory flows.</summary>
    PendingReview = 0,
    /// <summary>Retrievable.</summary>
    Published = 1,
    /// <summary>Flagged by safety checks (e.g. suspected prompt injection) - blocked until reviewed.</summary>
    Quarantined = 2,
    /// <summary>Superseded by a newer version.</summary>
    Superseded = 3,
}

public sealed class KnowledgeDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ExternalId { get; set; } = string.Empty;
    public string VendorCode { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public string ContentType { get; set; } = "text/markdown";
    public string ContentHash { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public SourceTrustLevel Trust { get; set; }
    public DocumentStatus Status { get; set; }
    public string? StatusReason { get; set; }
    public List<string> ProductCodes { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public DateOnly? RetrievedOn { get; set; }
    public DateTimeOffset IngestedAt { get; set; } = DateTimeOffset.UtcNow;
    public string IngestedBy { get; set; } = "system";
    public bool IsDemonstrationData { get; set; }
    public List<KnowledgeChunk> Chunks { get; set; } = [];
}

public sealed class KnowledgeChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public int Ordinal { get; set; }
    public string? Section { get; set; }
    public string Content { get; set; } = string.Empty;
    public List<string> ProductCodes { get; set; } = [];
    public List<string> CapabilityCodes { get; set; } = [];
    /// <summary>Embedding vector (optional - only when an embedding model is configured).</summary>
    public float[]? Embedding { get; set; }
}

/// <summary>A retrieval hit with full provenance.</summary>
public sealed record KnowledgeHit(
    Guid ChunkId,
    Guid DocumentId,
    string VendorCode,
    string DocumentTitle,
    string? SourceUrl,
    string? Section,
    string Snippet,
    double Score,
    SourceTrustLevel Trust,
    bool IsDemonstrationData,
    IReadOnlyList<string> ProductCodes,
    IReadOnlyList<string> CapabilityCodes);
