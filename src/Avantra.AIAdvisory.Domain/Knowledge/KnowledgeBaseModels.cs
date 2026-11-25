namespace Avantra.AIAdvisory.Domain.Knowledge;

/// <summary>
/// A knowledge base you can add at runtime (e.g. planview, salesforce, outsystems, internal-playbooks).
/// Each one has its own search index. If a vendor pack with the same code exists, full product advisory is enabled.
/// </summary>
public sealed class KnowledgeBaseDefinition
{
    /// <summary>Lower-case slug, e.g. "planview". Also used as the vendor code for documents.</summary>
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string IndexName { get; set; } = string.Empty;
    /// <summary>Optional folder that is synced on a schedule (files added/changed there are ingested).</summary>
    public string? SourceFolder { get; set; }
    /// <summary>0 = no scheduled sync.</summary>
    public double SyncIntervalHours { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSyncedAt { get; set; }
}

public enum IngestionJobState { Queued = 0, Running = 1, Succeeded = 2, PartiallySucceeded = 3, Failed = 4, Interrupted = 5 }

/// <summary>Persisted status of one ingestion job (upload, URL, folder sync).</summary>
public sealed class IngestionJobRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string KnowledgeBaseCode { get; set; } = string.Empty;
    public string Kind { get; set; } = "upload";
    public string Trigger { get; set; } = "api";
    public IngestionJobState State { get; set; } = IngestionJobState.Queued;
    public int Items { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int PendingReview { get; set; }
    public int Quarantined { get; set; }
    public int Failed { get; set; }
    /// <summary>JSON array of per-item results.</summary>
    public string? DetailsJson { get; set; }
    public string? Error { get; set; }
    public string RequestedBy { get; set; } = "system";
    public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
